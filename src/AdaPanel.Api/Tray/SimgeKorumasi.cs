using System.Runtime.InteropServices;

namespace AdaPanel.Api.Tray;

/// <summary>
/// Explorer yeniden başladığında tepsi simgesini geri koyar.
/// </summary>
/// <remarks>
/// <para>
/// Windows'ta kabuk (explorer.exe) yeniden başladığında bildirim alanındaki BÜTÜN simgeler
/// silinir. Uygulamalar bunu, kabuğun yayınladığı <c>TaskbarCreated</c> penceresi mesajıyla
/// öğrenip simgelerini yeniden eklemek zorundadır. Ele alınmazsa simge bir daha gelmez ve
/// kullanıcı açısından program "kendiliğinden kayboldu" görünür — oysa süreç çalışmaya
/// devam etmektedir.
/// </para>
/// <para>
/// Explorer'ın yeniden başlaması nadir bir olay değil: kabuk çöktüğünde Windows onu kendisi
/// yeniden başlatır, ayrıca bazı güncellemeler ve görev çubuğu ayar değişiklikleri de aynı
/// sonucu doğurur. Panel çöktüğünde başvurulacak aracın, kabuk bir kez çöktü diye ortadan
/// kaybolması kabul edilemez.
/// </para>
/// </remarks>
public sealed class SimgeKorumasi : NativeWindow, IDisposable
{
    private readonly NotifyIcon simge;
    private readonly uint taskbarCreated;

    public SimgeKorumasi(NotifyIcon simge)
    {
        this.simge = simge;

        // Mesajın numarası sabit değildir; kabuk onu isimle kaydeder ve numarayı sistem verir.
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Mesajı alabilmek için bir pencereye ihtiyaç var; görünmeyen bir mesaj penceresi yeter.
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message mesaj)
    {
        if (taskbarCreated != 0 && (uint)mesaj.Msg == taskbarCreated)
        {
            // Simgeyi ÖNCE kaldırıp sonra eklemek gerekiyor: kabuk kendi kaydını sildiği için
            // NotifyIcon hâlâ "görünür" sanıyor ve doğrudan true atamak hiçbir şey yapmıyor.
            try
            {
                simge.Visible = false;
                simge.Visible = true;
            }
            catch (Exception)
            {
                // Simge yok edilmişse (program kapanıyor) yapacak bir şey yok; burada fırlayan
                // bir istisna kabuğun mesaj döngüsünü bozar.
            }
        }

        base.WndProc(ref mesaj);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegisterWindowMessage(string ad);
}
