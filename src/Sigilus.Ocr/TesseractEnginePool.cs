using System.Collections.Concurrent;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;

namespace Sigilus.Ocr;

/// <summary>
/// Pool de <see cref="TesseractOcrEngine"/> para paralelismo real. Cada
/// instância carrega seus próprios pesos/caches (caro), então o pool cria
/// sob demanda até <paramref name="maxConcurrency"/> instâncias e as
/// reutiliza. <see cref="Recognize"/> bloqueia esperando um slot livre.
/// </summary>
public sealed class TesseractEnginePool : IOcrEngine, IDisposable
{
    private readonly string _tessdataPath;
    private readonly string _language;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentQueue<TesseractOcrEngine> _available = new();
    private readonly List<TesseractOcrEngine> _allCreated = new();
    private readonly object _createLock = new();
    private readonly int _max;
    private int _created;

    public TesseractEnginePool(string tessdataPath, int maxConcurrency, string language = "por")
    {
        _tessdataPath = tessdataPath;
        _language = language;
        _max = Math.Max(1, maxConcurrency);
        _slots = new SemaphoreSlim(_max, _max);
    }

    public IReadOnlyList<TextRun> Recognize(
        ReadOnlyMemory<byte> pngBitmap,
        int pageIndex,
        float pageWidthPts,
        float pageHeightPts,
        CancellationToken ct)
    {
        _slots.Wait(ct);
        var engine = Rent();
        try
        {
            return engine.Recognize(pngBitmap, pageIndex, pageWidthPts, pageHeightPts, ct);
        }
        finally
        {
            _available.Enqueue(engine);
            _slots.Release();
        }
    }

    private TesseractOcrEngine Rent()
    {
        if (_available.TryDequeue(out var e)) return e;
        lock (_createLock)
        {
            if (_available.TryDequeue(out e)) return e;
            if (_created < _max)
            {
                e = new TesseractOcrEngine(_tessdataPath, _language);
                _allCreated.Add(e);
                _created++;
                return e;
            }
        }
        // Pool cheio mas nenhum livre — espera (loop curto; semáforo evita starvation real).
        while (!_available.TryDequeue(out e)) Thread.Yield();
        return e;
    }

    public void Dispose()
    {
        foreach (var e in _allCreated) e.Dispose();
        _slots.Dispose();
    }
}
