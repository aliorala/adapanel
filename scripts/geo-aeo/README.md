# AdaPanel GEO/AEO adaptörü

Node.js 20+ yalnız build sırasında kullanılır, dış bağımlılık yoktur.
Python üreticisi güncel TOPICS/PAGES, metadata ve modified kaynaklarından
TR/EN config oluşturur; vendor `validateConfig` doğrular. Kit HTML renderer'ı
kullanılmaz: mevcut canonical/hreflang ve Python schema kimlikleri korunur.
Güncel profil `artifacts/geo-aeo/adapanel.profile.json` içine çıkar; public değildir.
`llms.txt` kapalıdır. robots kuralları kimlik doğrulaması sağlamaz.

PowerShell, proje kökünden:

```powershell
$env:ADAPANEL_NODE = (Get-Command node.exe -ErrorAction Stop).Source
python scripts/build-website.py
python scripts/test-website.py
& $env:ADAPANEL_NODE --test scripts/geo-aeo/test-measure.mjs
& $env:ADAPANEL_NODE scripts/geo-aeo/measure.mjs scripts/geo-aeo/profiles/adapanel.questions.json scripts/geo-aeo/profiles/adapanel.observations.ndjson
```

Node PATH üzerinde ise ADAPANEL_NODE gerekmez. Node bulunmazsa build açık hata verir.
6 sabit soru × 4 motor × 3 tekrar = 72 planlı gözlem. Başlangıç dosyası bilerek
boştur: coverage 0, diğer oranlar null ve durum `ölçülmedi` olmalıdır.
Her dönem için ayrı NDJSON oluşturun; başlangıç dosyasına sentetik veri yazmayın.
Kit `parseNdjson` ve `summarize` bu yerel CLI tarafından gerçekten kullanılır.
Her measured/error kaydı kitin README.txt sözleşmesine ek olarak plandaki
`planVersion` ve `conditions` nesnesini aynen taşımalıdır. `evidenceRef`, NDJSON
dosyasına göre mevcut yerel dosyaya (ör. `../evidence/2026-11-01-answer.txt`)
işaret etmelidir. Kanıt public çıktı dışında olmalıdır. Sürüm/koşul uyuşmazlığı,
eksik kanıt, yinelenen veya plan dışı kayıt reddedilir. Error başarı sayılmaz;
Yalnız error kayıtları varsa durum `ölçülmedi` kalır; en az bir measured kaydıyla
`ölçüldü` olur. Error kayıtları sonuç oranlarının paydasına katılmaz.
yalnız adapanel.host sınırlarındaki gerçek atıflar citationRate'e katılır.

Vendor kaynağı: kullanıcı tarafından sağlanan geo-aeo-kit/src, 2026-10-29.
Yalnız ortak kaynak kod taşındı; diğer proje profilleri/generated HTML alınmadı.
