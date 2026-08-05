/**
 * Panoya kopyalama — GÜVENLİ OLMAYAN bağlamda da çalışır.
 *
 * `navigator.clipboard` yalnızca güvenli bağlamda (HTTPS ya da localhost) vardır. Panel ilk
 * kurulduğunda henüz sertifika almamış olabilir ve tam da o anda kullanıcıya yeni sitenin
 * veritabanı parolası gösterilir; "Kopyala" düğmesi orada sessizce ölüyordu.
 *
 * Bu yüzden eski `execCommand("copy")` yolu bir yedek olarak duruyor: tarayıcılarda kullanımdan
 * kaldırılmış sayılsa da HTTP üzerinde hâlâ çalışan tek yol odur.
 *
 * @returns Kopyalandıysa true. Çağıran, SONUCA göre geri bildirim vermelidir — "kopyalandı"
 * demek, kopyalandığını görmeden verilebilecek bir söz değildir.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // İzin reddedildi ya da bağlam güvenli değil: aşağıdaki yedek yol denenir.
    }
  }

  try {
    const alan = document.createElement("textarea");
    alan.value = text;
    // Ekran dışında ama ODAKLANABİLİR olmalı: display:none ya da hidden bir öğeden seçim yapılamaz.
    alan.setAttribute("readonly", "");
    alan.style.position = "fixed";
    alan.style.top = "-1000px";
    alan.style.opacity = "0";
    document.body.appendChild(alan);
    alan.select();
    const oldu = document.execCommand("copy");
    document.body.removeChild(alan);
    return oldu;
  } catch {
    return false;
  }
}
