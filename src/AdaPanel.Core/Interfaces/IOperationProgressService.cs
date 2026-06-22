using AdaPanel.Core.Dtos.Common;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Tek yuvalı (aynı anda bir işlem) canlı ilerleme izleyicisi. Uzun süren işlemler
/// (ilk kurulum, yazılım kurulumları) adımlarını buraya yazar; frontend yoklayarak gösterir.
/// Singleton'dır ve thread-safe çalışır.
/// </summary>
public interface IOperationProgressService
{
    /// <summary>Yeni bir işlem başlatır (önceki adımları temizler).</summary>
    void Begin(string title);

    /// <summary>Yeni bir adımı "çalışıyor" olarak ekler.</summary>
    void StartStep(string name);

    /// <summary>Adı verilen adımı tamamlar (başarılı/başarısız). Yoksa eklenir.</summary>
    void CompleteStep(string name, bool success, string detail = "");

    /// <summary>İşlemi başarıyla bitirir.</summary>
    void Complete();

    /// <summary>İşlemi hatayla bitirir.</summary>
    void Fail(string error);

    /// <summary>Anlık ilerleme görüntüsü.</summary>
    OperationProgressDto Snapshot();
}
