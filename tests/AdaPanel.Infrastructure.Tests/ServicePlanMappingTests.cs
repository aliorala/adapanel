using System.Reflection;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Servis planı isteğinin kayda EKSİKSİZ yansıması.
/// </summary>
/// <remarks>
/// <para>
/// Bu test gerçek bir hatadan doğdu. Plana üç yeni limit eklendi (FTP hesabı, aylık trafik,
/// geçerlilik süresi) ama servisteki alan-alan eşleştirme güncellenmedi: istek dolu gelirken
/// kayda <b>0</b> yazılıyor, API "başarılı" dönüyor ve kullanıcı limiti koyduğunu sanıyordu.
/// Trafik kotası özelliği bu yüzden fiilen ÖLÜYDÜ — kota hiç saklanamadığı için hiç uygulanamazdı.
/// </para>
/// <para>
/// Birim testleri bunu yakalayamamıştı çünkü hepsi eşleştirmenin ötesindeki mantığı test ediyordu.
/// Uçtan uca çağrı yakaladı. Aşağıdaki ilk test o boşluğu kapatır: <b>yansımayla</b> çalıştığı için
/// plana ileride eklenecek her yeni alan, eşleştirilmediği anda kırmızı yanar.
/// </para>
/// </remarks>
public sealed class ServicePlanMappingTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ada-plan-{Guid.NewGuid():N}");
    private readonly ServicePlanService servis;

    public ServicePlanMappingTests()
    {
        Directory.CreateDirectory(dataRoot);
        servis = new ServicePlanService(new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot })));
    }

    public void Dispose()
    {
        try { Directory.Delete(dataRoot, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public async Task Istekteki_HER_sayisal_limit_plana_yansir()
    {
        // Yansıma kullanılıyor: yeni bir limit eklenip eşleştirilmezse bu test kendiliğinden
        // kırmızı yanar. Elle sayılan bir liste, tam da unutulan alanı atlardı.
        var istekAlanlari = typeof(ServicePlanRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(item => item.PropertyType == typeof(int))
            .ToArray();

        Assert.NotEmpty(istekAlanlari);

        var istek = new ServicePlanRequest { Name = "Sinama" };
        // Her alana FARKLI bir değer: yanlış alana yazma (kopyala-yapıştır hatası) da yakalansın.
        var beklenen = new Dictionary<string, int>();
        var deger = 11;
        foreach (var alan in istekAlanlari)
        {
            alan.SetValue(istek, deger);
            beklenen[alan.Name] = deger;
            deger += 7;
        }

        var plan = await servis.CreatePlanAsync(istek);

        foreach (var (ad, sayi) in beklenen)
        {
            var planAlani = typeof(ServicePlanDto).GetProperty(ad, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(planAlani is not null, $"ServicePlanDto'da '{ad}' alanı yok; istek ile plan uyuşmuyor.");
            Assert.Equal(sayi, (int)planAlani!.GetValue(plan)!);
        }
    }

    [Fact]
    public async Task Guncelleme_de_HER_alani_yazar()
    {
        // Oluşturma ile güncelleme AYRI eşleştirmeler; biri düzeltilip diğeri unutulabilir.
        var olusan = await servis.CreatePlanAsync(new ServicePlanRequest { Name = "Ilk" });

        var guncel = await servis.UpdatePlanAsync(olusan.Id, new ServicePlanRequest
        {
            Name = "Guncel",
            MaxSites = 3,
            MaxDiskMb = 2048,
            MaxDatabases = 4,
            MaxMailboxes = 6,
            MaxScheduledTasks = 8,
            MaxFtpUsers = 9,
            MaxTrafficGbPerMonth = 25,
            ValidityDays = 90
        });

        Assert.Equal("Guncel", guncel.Name);
        Assert.Equal(9, guncel.MaxFtpUsers);
        Assert.Equal(25, guncel.MaxTrafficGbPerMonth);
        Assert.Equal(90, guncel.ValidityDays);

        // DİSKE de yazılmış olmalı: bellekteki nesneyi döndürüp kaydetmemek de aynı sınıf hatadır.
        var diskten = await servis.GetPlanAsync(olusan.Id);
        Assert.Equal(25, diskten!.MaxTrafficGbPerMonth);
        Assert.Equal(90, diskten.ValidityDays);
    }

    [Fact]
    public async Task Negatif_degerler_sifira_cekilir()
    {
        var plan = await servis.CreatePlanAsync(new ServicePlanRequest
        {
            Name = "Negatif",
            MaxTrafficGbPerMonth = -5,
            ValidityDays = -1
        });

        Assert.Equal(0, plan.MaxTrafficGbPerMonth);
        Assert.Equal(0, plan.ValidityDays);
    }
}
