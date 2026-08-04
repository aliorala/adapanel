namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// Zamanlanmış görevlerin çalışacağı ortam hakkında ölçülmüş bilgi.
/// <para>
/// Görevler SYSTEM olarak çalışır; kullanıcının kendi oturumunda kurduğu araçlar orada YOKTUR.
/// Bu fark, kullanıcı kendi terminalinde denediğinde her şey çalıştığı için neredeyse hiç fark
/// edilmez — panel bunu ölçüp söylemezse teşhis kullanıcıya kalır.
/// </para>
/// </summary>
public sealed class TaskEnvironmentDto
{
    /// <summary>Sunucuda bulunan python.exe yolları; en yeni sürüm başta.</summary>
    public IReadOnlyList<string> PythonInterpreters { get; set; } = [];

    /// <summary>
    /// SYSTEM'in PATH'inden çözülen python.exe. Boşsa görevler <c>python</c> yazımını çözemez.
    /// </summary>
    public string PythonOnPath { get; set; } = string.Empty;

    /// <summary>
    /// Python kurulu ama SYSTEM PATH'inde değil: onarılabilir tek durum budur. Hiç kurulu değilse
    /// panelin yapabileceği bir şey yoktur, kurulum kullanıcının kararıdır.
    /// </summary>
    public bool CanRepairPython => PythonInterpreters.Count > 0 && string.IsNullOrWhiteSpace(PythonOnPath);
}
