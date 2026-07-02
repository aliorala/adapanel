namespace AdaPanel.Core.Dtos.System;

/// <summary>
/// Tek bir zaman noktasındaki sistem ölçüm örneği (kalıcı izleme geçmişi). Arka plan worker'ı dakikada bir
/// biriktirir; izleme ekranı trend grafiklerini bununla doldurur (sayfa yenilense de dolu gelir).
/// </summary>
public sealed class SystemHistorySampleDto
{
    public DateTime TimestampUtc { get; set; }

    public double CpuPercent { get; set; }

    public double MemoryPercent { get; set; }

    public double NetworkDownBytesPerSec { get; set; }

    public double NetworkUpBytesPerSec { get; set; }
}
