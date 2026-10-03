<template>
  <section class="panel module-shell toolkit">
    <!-- `h3` -> `h2`. SAYILDI: bu görünüm `WebsiteWorkspaceView`in araç yuvasında çiziliyor ve o
         sayfada BAŞKA hiçbir başlık yok (site adı `<strong>`, sekmeler `<button>`). Yani sayfanın
         en üst başlığı buydu ama panelin başlık merdiveninde bir alt basamakta duruyordu.
         Aynı yuvaya giren 7 kardeş araçtan 6'sı (PHP, ASP, Cron, Dosyalar, Uygulamalar, Yedek)
         `h2` kullanıyor; kullanıcı araçlar arasında geçerken başlık boyu 19.2px ile 15.9px
         arasında zıplıyordu. -->
    <div class="module-head">
      <div>
        <h2>{{ t("panel.toolkit.title") }}</h2>
        <p class="meta">{{ t("panel.toolkit.subtitle") }}</p>
      </div>
    </div>

    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="tab === 'composer'" :class="{ active: tab === 'composer' }" @click="tab = 'composer'">{{ t("panel.toolkit.tabs.composer") }}</button>
        <button role="tab" :aria-selected="tab === 'artisan'" :class="{ active: tab === 'artisan' }" @click="tab = 'artisan'">{{ t("panel.toolkit.tabs.artisan") }}</button>
        <button role="tab" :aria-selected="tab === 'node'" :class="{ active: tab === 'node' }" @click="tab = 'node'">{{ t("panel.toolkit.tabs.node") }}</button>
        <button role="tab" :aria-selected="tab === 'wordpress'" :class="{ active: tab === 'wordpress' }" @click="selectWordPress">{{ t("panel.toolkit.tabs.wordpress") }}</button>
        <button role="tab" :aria-selected="tab === 'wpcli'" :class="{ active: tab === 'wpcli' }" @click="tab = 'wpcli'">{{ t("panel.toolkit.tabs.wpcli") }}</button>
        <button role="tab" :aria-selected="tab === 'queue'" :class="{ active: tab === 'queue' }" @click="selectQueue">{{ t("panel.toolkit.tabs.queue") }}</button>
      </div>

      <CommandConsole
        v-if="tab === 'composer'"
        :title="t('panel.toolkit.tabs.composer')"
        :hint="t('panel.toolkit.composerHint')"
        placeholder="update, require monolog/monolog, dump-autoload"
        :presets="['install --no-dev --optimize-autoloader', 'update', 'require ', 'remove ', 'dump-autoload -o', 'show']"
        :runner="composerRunner" />

      <CommandConsole
        v-else-if="tab === 'artisan'"
        :title="t('panel.toolkit.tabs.artisan')"
        :hint="t('panel.toolkit.artisanHint')"
        placeholder="migrate, cache:clear, key:generate"
        :presets="['migrate', 'migrate --force', 'cache:clear', 'config:cache', 'route:list', 'key:generate', 'optimize']"
        :runner="artisanRunner" />

      <CommandConsole
        v-else-if="tab === 'node'"
        :title="t('panel.toolkit.tabs.node')"
        :hint="t('panel.toolkit.nodeHint')"
        placeholder="install, run build, run dev"
        :presets="['install', 'ci', 'run build', 'run dev', '-v']"
        :runner="nodeRunner" />

      <CommandConsole
        v-else-if="tab === 'wpcli'"
        :title="t('panel.toolkit.tabs.wpcli')"
        :hint="t('panel.toolkit.wpcliHint')"
        placeholder="plugin list, core update, cache flush"
        :presets="['core version', 'plugin list', 'plugin update --all', 'theme list', 'cache flush', 'core update']"
        :runner="wpcliRunner" />

      <div v-else-if="tab === 'wordpress'" class="form-surface wp-panel">
        <!-- Sonuç ekrana SABİTLENİR (`page-notice`, bkz. styles/base.css): WordPress sekmesi çok
             uzun (eklenti + tema listeleri onlarca satır). Kullanıcı listenin dibindeki
             "Güncelle"/"Sil" düğmesine basıp sonucu görmüyor, ikinci kez basıyordu. -->
        <p v-if="wpNotice" :class="['notice', 'page-notice', { error: wpNoticeError }]" :role="wpNoticeError ? 'alert' : 'status'">{{ wpNotice }}</p>
        <p v-if="wpLoading" class="empty-state" role="status" aria-live="polite">{{ t("panel.wpkit.loading") }}</p>
        <SectionLoadError v-else-if="wpLoad.message.value" :message="wpLoad.message.value" @retry="loadWp" />
        <!-- `notice warn` TANIMSIZDI: `.notice`in kehribar sürümünün adı `warn-banner` (base.css)
             ve `warn` diye bir varyant hiçbir yerde yok. Yani "burası WordPress değil" uyarısı
             MAVİ BİLGİ şeridi olarak çiziliyordu — bir bilgi kutusuyla aynı renkte. -->
        <p v-else-if="wp && !wp.isWordPress" class="notice warn-banner" role="status">{{ t("panel.wpkit.notWordpress") }}</p>

        <template v-else-if="wp">
          <!-- ROZETLER PANELİN ORTAK ROZETİNE (`status-pill`) TAŞINDI; yerel `pill`/`stopped`
               kopyası silindi. Kopya üç şeyi birden bozuyordu:
               (1) AYNI AD, ÜÇ FARKLI GÖRÜNÜM — `.pill` bu depoda üç bileşende (AppToolkit, Asp,
                   Sites) ayrı ayrı tanımlıydı ve tanımlar birbirini tutmuyordu.
               (2) DURUM DİLİ TERSTİ — "etkin/açık" olan her şey MAVİ (vurgu) çiziliyordu; oysa
                   panelin geri kalanında (Güvenlik özet şeridi, disk tablosu, sunucu sağlığı)
                   açık = yeşil, kapalı/bekleyen = kehribar. Kullanıcı "XML-RPC kapatıldı"
                   rozetini bir bilgi etiketi sanıyordu.
               (3) ÖLÇÜLDÜ, AÇIK TEMA: yerel kural `color: var(--accent-1)` idi ve zemini
                   `accent %14` tonuydu; bu ikili beyaz kartta ~3.4:1 veriyordu (AA eşiği 4.5).
                   Ortak rozet `--ok-text`/`--warn-text` kullanır, o belirteçler tam da bu iş
                   için ölçülmüş hâllerdir.
               Yeşil nokta (`state-dot`) da kalktı: rozet zaten renk + SÖZCÜK taşıyor, nokta
               üçüncü bir kez aynı şeyi söylüyordu. -->
          <div class="wp-status">
            <span class="status-pill">WordPress {{ wp.version || "?" }}</span>
            <span v-if="wp.updateAvailable" class="status-pill warn">{{ t("panel.wpkit.updateAvailable", { v: wp.latestVersion }) }}</span>
            <span v-else class="status-pill ok">{{ t("panel.wpkit.upToDate") }}</span>
            <a v-if="wp.adminUrl" :href="wp.adminUrl" target="_blank" rel="noopener" class="wp-admin-link">{{ t("panel.wpkit.openAdmin") }} ↗</a>
          </div>

          <div class="wp-actions">
            <button class="compact" v-bind="writeGate(!!wpBusy)" @click="wpLogin">{{ wpBusy === 'login' ? t("panel.wpkit.working") : t("panel.wpkit.login") }}</button>
            <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpUpdate">{{ wpBusy === 'update' ? t("panel.wpkit.working") : t("panel.wpkit.update") }}</button>
            <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpHarden">{{ wpBusy === 'harden' ? t("panel.wpkit.working") : t("panel.wpkit.harden") }}</button>
            <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpToggleMaintenance">{{ wp.maintenanceMode ? t("panel.wpkit.maintenanceOff") : t("panel.wpkit.maintenanceOn") }}</button>
          </div>

          <!-- BÖLÜM BAŞLIKLARI `h4` OLDU (bu blokta ve aşağıdaki yedi blokta).
               ÖLÇÜLDÜ: sekiz bölümün başlığı `field-label` ile yazılıyordu — yani bir bölümün ADI
               ile o bölümdeki bir ALANIN etiketi BİREBİR aynı boyda (0.92rem), aynı kalınlıkta
               (600), aynı renkteydi. Sekiz bölüm alt alta, aralarında yalnız 1px çizgi; sayfanın
               iskeleti gözle görünmüyordu. `h4` panelin başlık merdivenine bağlıdır
               (base.css: `--fs-h4` = 14.3px / 640) ve `h2` ile alan etiketi arasına gerçek bir
               basamak koyar. Metinler DEĞİŞMEDİ, yalnız rütbeleri düzeldi. -->
          <div class="wp-form">
            <h4>{{ t("panel.wpkit.securityStatus") }}</h4>
            <div class="wp-list">
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_file_edit") }}</span><span :class="['status-pill', wp.security.fileEditorDisabled ? 'ok' : 'warn']">{{ wp.security.fileEditorDisabled ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_version") }}</span><span :class="['status-pill', wp.security.versionHidden ? 'ok' : 'warn']">{{ wp.security.versionHidden ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_xmlrpc") }}</span><span :class="['status-pill', wp.security.xmlRpcPingbackDisabled ? 'ok' : 'warn']">{{ wp.security.xmlRpcPingbackDisabled ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_author") }}</span><span :class="['status-pill', wp.security.authorScanBlocked ? 'ok' : 'warn']">{{ wp.security.authorScanBlocked ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
            </div>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.autoUpdates") }}</h4>
            <div class="wp-row">
              <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpAutoUpdate(true)">{{ wpBusy === 'autoOn' ? t("panel.wpkit.working") : t("panel.wpkit.autoUpdatesOn") }}</button>
              <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpAutoUpdate(false)">{{ wpBusy === 'autoOff' ? t("panel.wpkit.working") : t("panel.wpkit.autoUpdatesOff") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.autoUpdatesHint") }}</small>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.installPlugin") }}</h4>
            <div class="wp-row">
              <input v-model.trim="wpForm.pluginSlug" placeholder="akismet" v-bind="writeGate(!!wpBusy)" />
              <button class="secondary compact" v-bind="writeGate(!!wpBusy || !wpForm.pluginSlug)" @click="wpInstallPlugin">{{ wpBusy === 'plugin' ? t("panel.wpkit.working") : t("panel.wpkit.install") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.installPluginHint") }}</small>
          </div>

          <!-- Eklenti/tema listesi BOŞKEN bölüm tamamen kayboluyordu: kullanıcı listenin
               yüklenmediğini mi yoksa gerçekten eklenti olmadığını mı bilemiyordu. -->
          <div class="wp-form">
            <h4>{{ t("panel.wpkit.plugins") }} ({{ wp.plugins.length }})</h4>
            <p v-if="wp.plugins.length === 0" class="empty-state">{{ t("panel.wpkit.noPlugins") }}</p>
            <div v-else class="wp-list">
              <div v-for="p in wp.plugins" :key="p.slug" class="wp-list-row">
                <!-- "Etkin" ARTIK GÖRÜNÜR BİR ROZET. Önceki hâlde durum iki kanaldan anlatılıyordu
                     ve ikisi de yetersizdi: 6px'lik yeşil bir nokta (renk körü için yok hükmünde,
                     ekran okuyucuya `aria-hidden`) ve YALNIZ ekran okuyucunun duyduğu `sr-only`
                     metin. Yani GÖZLE bakan bir kullanıcı için etkin/pasif ayrımı tek kanaldı:
                     renk. Şimdi aynı bilgi panelin ortak rozetiyle, SÖZCÜKLE yazılı; `sr-only`
                     gereksizleştiği için kaldırıldı (aynı metin artık görünür).
                     PASİF eklenti "uyarı" tonu ALMAZ, nötr kalır: pasif bir eklenti bir arıza
                     değildir, her satırı boyamak gürültü olurdu. -->
                <span class="wp-list-name">{{ p.name }} <small v-if="p.version">v{{ p.version }}</small></span>
                <span class="wp-list-actions">
                  <span :class="['status-pill', p.active ? 'ok' : '']">{{ p.active ? t("panel.wpkit.activeLabel") : t("panel.status.disabled") }}</span>
                  <button v-if="p.active" class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('plugin', 'deactivate', p.slug)">{{ t("panel.wpkit.deactivate") }}</button>
                  <button v-else class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('plugin', 'activate', p.slug)">{{ t("panel.wpkit.activate") }}</button>
                  <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('plugin', 'update', p.slug)">{{ t("panel.wpkit.compUpdate") }}</button>
                  <!-- SİLME AYRI TONDA. Bir satırda dört düğme yan yana ve HEPSİ aynı gri kutu
                       boyundaydı; yanlış olana tek tık, eklentiyi diskten siliyordu. Metin tek
                       başına yetmiyordu ("Güncelle" / "Sil" aynı uzunlukta ve aynı hizada).
                       Tehlike tonu, panelin geri kalanındaki silme düğmeleriyle (Güvenlik >
                       güvenlik duvarı kuralı) aynı dildir. -->
                  <button v-if="!p.active" class="danger compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('plugin', 'delete', p.slug)">{{ t("panel.wpkit.compDelete") }}</button>
                </span>
              </div>
            </div>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.themes") }} ({{ wp.themes.length }})</h4>
            <p v-if="wp.themes.length === 0" class="empty-state">{{ t("panel.wpkit.noThemes") }}</p>
            <div v-else class="wp-list">
              <div v-for="th in wp.themes" :key="th.slug" class="wp-list-row">
                <!-- Aynı gerekçe: etkin tema yalnız renkle işaretliydi. Etkin temanın rozeti
                     zaten vardı (`pill`) ama PASİF temada hiçbir rozet yoktu, yani iki durum
                     farklı SAYIDA ögeyle anlatılıyor ve sütun hizası kayıyordu. -->
                <span class="wp-list-name">{{ th.name }} <small v-if="th.version">v{{ th.version }}</small></span>
                <span class="wp-list-actions">
                  <span :class="['status-pill', th.active ? 'ok' : '']">{{ th.active ? t("panel.wpkit.activeLabel") : t("panel.status.disabled") }}</span>
                  <button v-if="!th.active" class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('theme', 'activate', th.slug)">{{ t("panel.wpkit.activate") }}</button>
                  <button class="secondary compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('theme', 'update', th.slug)">{{ t("panel.wpkit.compUpdate") }}</button>
                  <button v-if="!th.active" class="danger compact" v-bind="writeGate(!!wpBusy)" @click="wpComponent('theme', 'delete', th.slug)">{{ t("panel.wpkit.compDelete") }}</button>
                </span>
              </div>
            </div>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.searchReplace") }}</h4>
            <div class="wp-row">
              <input v-model.trim="wpForm.srFrom" :placeholder="t('panel.wpkit.srFromPlaceholder')" v-bind="writeGate(!!wpBusy)" />
              <input v-model.trim="wpForm.srTo" :placeholder="t('panel.wpkit.srToPlaceholder')" v-bind="writeGate(!!wpBusy)" />
              <button class="secondary compact" v-bind="writeGate(!!wpBusy || !wpForm.srFrom || !wpForm.srTo)" @click="wpSearchReplace">{{ wpBusy === 'sr' ? t("panel.wpkit.working") : t("panel.wpkit.replace") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.searchReplaceHint") }}</small>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.resetPassword") }}</h4>
            <div class="wp-row">
              <input v-model.trim="wpForm.adminUser" placeholder="admin" v-bind="writeGate(!!wpBusy)" />
              <button class="secondary compact" v-bind="writeGate(!!wpBusy || !wpForm.adminUser)" @click="wpResetPassword">{{ wpBusy === 'pass' ? t("panel.wpkit.working") : t("panel.wpkit.reset") }}</button>
            </div>
            <div v-if="wpNewPassword" class="notice wp-cred" role="status">{{ t("panel.wpkit.newPassword") }}: <code>{{ wpNewPassword }}</code><br /><small>{{ t("panel.wpkit.newPasswordHint") }}</small></div>
          </div>

          <div class="wp-form">
            <h4>{{ t("panel.wpkit.clone") }}</h4>
            <div class="wp-row">
              <input v-model.trim="wpClone.targetSiteName" :placeholder="t('panel.wpkit.cloneSiteName')" v-bind="writeGate(!!wpBusy)" />
              <input v-model.trim="wpClone.targetHostName" :placeholder="t('panel.wpkit.cloneHostPlaceholder')" v-bind="writeGate(!!wpBusy)" />
              <button class="secondary compact" v-bind="writeGate(!!wpBusy || !wpClone.targetSiteName || !wpClone.targetHostName)" @click="wpDoClone">{{ wpBusy === 'clone' ? t("panel.wpkit.working") : t("panel.wpkit.cloneBtn") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.cloneHint") }}</small>
            <div v-if="wpCloneResult" class="notice wp-cred" role="status">
              <a :href="wpCloneResult.targetUrl" target="_blank" rel="noopener">{{ wpCloneResult.targetUrl }}</a>
              · DB: <code>{{ wpCloneResult.targetDatabase }}</code>
              · {{ t("panel.wpkit.cloneUser") }}: <code>{{ wpCloneResult.databaseUser }}</code>
              · {{ t("panel.wpkit.clonePass") }}: <code>{{ wpCloneResult.databasePassword }}</code>
            </div>
          </div>

          <!-- BU SEKMENİN TEK YIKICI EYLEMİ. Kaynak siteyi VAR OLAN başka bir sitenin üzerine
               yazar (staging -> canlı) ve geri dönüşü yalnız otomatik yedektir.
               ÖNCEKİ HÂLİ ÖLÇÜLDÜ: düğme yukarıdaki yedi bölümdeki "Kur"/"Değiştir"/"Sıfırla"
               ile BİREBİR aynı gri `secondary compact` kutuydu, sırası da aynıydı (satırın
               sonu), ve tek uyarı `<small class="hint">` içinde bir "⚠" karakteriydi — panelin
               en küçük, en silik metin rütbesi. Yani ekranın en tehlikeli düğmesi, en sessiz
               şekilde anlatılıyordu.
               ŞİMDİ üç kanal birden: (1) uyarı, panelin ortak kehribar şeridine (`warn-banner`)
               taşındı ve düğmeden ÖNCE okunuyor; (2) düğme tehlike tonunda; (3) düğme kendi
               satırında ve `margin-left:auto` ile SABİT bir yerde (`destructive-slot`), yani
               yanlışlıkla basılan komşu bir düğmenin yerinde durmuyor.
               Onay diyaloğu ve hedef adı yazdırma zorunluluğu AYNEN duruyor (bkz. `wpDoCopy`). -->
          <div class="wp-form">
            <h4>{{ t("panel.wpkit.copyData") }}</h4>
            <p class="notice warn-banner" role="note">{{ t("panel.wpkit.copyHint") }}</p>
            <div class="wp-row">
              <input v-model.trim="wpCopy.targetSiteName" :placeholder="t('panel.wpkit.copyTarget')" v-bind="writeGate(!!wpBusy)" />
              <label class="wp-check"><input v-model="wpCopy.files" type="checkbox" v-bind="writeGate(!!wpBusy)" /> {{ t("panel.wpkit.copyFiles") }}</label>
              <label class="wp-check"><input v-model="wpCopy.database" type="checkbox" v-bind="writeGate(!!wpBusy)" /> {{ t("panel.wpkit.copyDatabase") }}</label>
            </div>
            <div class="wp-row">
              <button class="danger compact destructive-slot" v-bind="writeGate(!!wpBusy || !wpCopy.targetSiteName || (!wpCopy.files && !wpCopy.database))" @click="wpDoCopy">{{ wpBusy === 'copy' ? t("panel.wpkit.working") : t("panel.wpkit.copyBtn") }}</button>
            </div>
            <div v-if="wpCopyResult" class="notice wp-cred" role="status">{{ t("panel.wpkit.copyDone") }}<br /><small>{{ t("panel.wpkit.copyBackup") }}: <code>{{ wpCopyResult.backupPath }}</code></small></div>
          </div>

          <pre v-if="wpOutput" class="wp-output">{{ wpOutput }}</pre>
        </template>
      </div>

      <!-- İKİ AYRI İŞÇİ, İKİ AYRI KUTU. Önceden ikisi TEK kutunun içindeydi ve aralarındaki tek
           ayrım, ikinci başlığın üstündeki 1px çizgiydi (`sched-head`). İki bağımsız servisin
           (kuyruk işçisi ve zamanlayıcı) durumu ve düğmeleri alt alta akınca, "Başlat" düğmesinin
           hangisini başlattığı ancak sırayı sayarak anlaşılıyordu. Panelin ortak `form-surface`
           kalıbı her servise kendi çerçevesini verir; `queue-panel` kopyası (kendi kenarlığı,
           kendi 12px yarıçapı, kendi `surface-1` zemini) silindi. -->
      <div v-else class="queue-stack">
        <p v-if="queueNotice" :class="['notice', 'page-notice', { error: queueNoticeError }]" :role="queueNoticeError ? 'alert' : 'status'">{{ queueNotice }}</p>

        <div class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.toolkit.queue.title") }}</h4>
              <p class="meta">{{ t("panel.toolkit.queue.desc") }}</p>
            </div>
            <!-- DURUM ROZETİ BAŞLIK SATIRINDA, panelin geri kalanıyla aynı yerde (Güvenlik >
                 Antivirüs, Siteler listesi). Eskiden düğmenin yanındaydı ve aynı yükseklikte,
                 aynı yuvarlaklıkta olduğu için TIKLANABİLİR sanılıyordu. -->
            <span v-if="queueLoaded" :class="['status-pill', queueRunning ? 'ok' : 'warn']">
              {{ queueRunning ? t("panel.toolkit.queue.statusRunning") : t("panel.toolkit.queue.statusStopped") }}
            </span>
            <span v-else class="status-pill">{{ t("panel.toolkit.queue.statusUnknown") }}</span>
          </div>

          <SectionLoadError v-if="queueLoad.message.value" :message="queueLoad.message.value" @retry="loadQueue" />

          <p class="hint">{{ t("panel.toolkit.queue.hint") }}</p>

          <div class="row-actions">
            <button v-if="!queueRunning" class="compact" v-bind="writeGate(queueBusy)" @click="startQueue">
              {{ queueBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.start") }}
            </button>
            <button v-else class="secondary compact" v-bind="writeGate(queueBusy)" @click="stopQueue">
              {{ queueBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.stop") }}
            </button>
          </div>
        </div>

        <div class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.toolkit.scheduler.title") }}</h4>
              <p class="meta">{{ t("panel.toolkit.scheduler.desc") }}</p>
            </div>
            <span v-if="schedulerLoaded" :class="['status-pill', schedulerRunning ? 'ok' : 'warn']">
              {{ schedulerRunning ? t("panel.toolkit.queue.statusRunning") : t("panel.toolkit.queue.statusStopped") }}
            </span>
            <span v-else class="status-pill">{{ t("panel.toolkit.queue.statusUnknown") }}</span>
          </div>

          <SectionLoadError v-if="schedulerLoad.message.value" :message="schedulerLoad.message.value" @retry="loadScheduler" />

          <p class="hint">{{ t("panel.toolkit.scheduler.hint") }}</p>

          <div class="row-actions">
            <button v-if="!schedulerRunning" class="compact" v-bind="writeGate(schedulerBusy)" @click="startScheduler">
              {{ schedulerBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.start") }}
            </button>
            <button v-else class="secondary compact" v-bind="writeGate(schedulerBusy)" @click="stopScheduler">
              {{ schedulerBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.stop") }}
            </button>
          </div>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import CommandConsole from "../components/CommandConsole.vue";
import {
  getLaravelScheduler,
  getQueueWorker,
  runArtisanCommand,
  runComposerCommand,
  runNodeCommand,
  runWpCliCommand,
  startLaravelScheduler,
  startQueueWorker,
  stopLaravelScheduler,
  stopQueueWorker
} from "../api/siteHosting";
import { getWordPress, setWordPressMaintenance, createWordPressLoginLink, cloneWordPress, hardenWordPress, copyWordPressData, type WordPressSite, type WordPressCloneResult, type WordPressCopyDataResult } from "../api/wordpress";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { openAsyncReachable } from "../utils/externalTab";

type ToolkitTab = "composer" | "artisan" | "node" | "wpcli" | "wordpress" | "queue";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
// resolveApiMessage TEK KAYNAKTAN (useNotice); bu dosyadaki elle kopyası kaldırıldı.
const { resolveApiMessage } = useNotice();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const tab = ref<ToolkitTab>("composer");

// Akıllı yönlendirme: kullanıcı hangi sekmede olursa olsun, komutun başına göre doğru çalıştırıcıya gönderilir.
// (ör. Composer sekmesinde "php artisan migrate" yazılsa bile Artisan'a gider.) Önek yoksa sekmenin
// kendi çalıştırıcısı kullanılır. Backend önekleri (composer/php artisan/npm) zaten kırpıyor.
type Runner = (site: string, command: string) => Promise<{ exitCode: number; output: string; error: string }>;
function routed(fallback: Runner) {
  return (command: string) => {
    const text = command.trim();
    if (/^composer\b/i.test(text)) return runComposerCommand(siteName.value, text);
    if (/^(php\s+artisan|artisan)\b/i.test(text)) return runArtisanCommand(siteName.value, text);
    if (/^npm\b/i.test(text)) return runNodeCommand(siteName.value, text);
    if (/^wp\b/i.test(text)) return runWpCliCommand(siteName.value, text);
    return fallback(siteName.value, text);
  };
}

const composerRunner = routed(runComposerCommand);
const artisanRunner = routed(runArtisanCommand);
const nodeRunner = routed(runNodeCommand);
const wpcliRunner = routed(runWpCliCommand);

// --- WordPress Toolkit (Plesk benzeri tek-tık aksiyonlar; mevcut wp-cli motorunu kullanır) ---
const wp = ref<WordPressSite | null>(null);
const wpLoading = ref(false);
const wpLoaded = ref(false);
// "Yükleniyor" / "WordPress değil" / "okunamadı" üç ayrı durum.
const wpLoad = useSectionError();
const wpBusy = ref<"" | "login" | "update" | "harden" | "plugin" | "sr" | "pass" | "maint" | "autoOn" | "autoOff" | "clone" | "comp" | "copy">("");
const wpOutput = ref("");
const wpNotice = ref("");
const wpNoticeError = ref(false);
const wpNewPassword = ref("");
const wpForm = reactive({ pluginSlug: "", srFrom: "", srTo: "", adminUser: "admin" });
const wpClone = reactive({ targetSiteName: "", targetHostName: "" });
const wpCloneResult = ref<WordPressCloneResult | null>(null);
const wpCopy = reactive({ targetSiteName: "", files: true, database: true });
const wpCopyResult = ref<WordPressCopyDataResult | null>(null);

function selectWordPress() {
  tab.value = "wordpress";
  if (!wpLoaded.value) {
    void loadWp();
  }
}

async function loadWp() {
  if (!siteName.value) {
    return;
  }
  wpLoading.value = true;
  try {
    wp.value = await getWordPress(siteName.value);
    wpLoaded.value = true;
    wpLoad.clear();
  } catch (error) {
    // "Okunamadı" BOŞ durumdan ayrı: hata yalnız geçici şeritte kalınca sekme bomboş görünüyor
    // ve kullanıcı sitede WordPress olmadığını sanıyordu.
    wp.value = null;
    wpLoad.capture(error);
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpLoading.value = false;
  }
}

function showWpNotice(message: string, isError: boolean) {
  wpNotice.value = message;
  wpNoticeError.value = isError;
}

// Tek bir wp-cli komutu çalıştırır, çıktıyı gösterir, exitCode==0 başarı sayılır.
async function runWp(command: string): Promise<boolean> {
  const result = await runWpCliCommand(siteName.value, command);
  wpOutput.value = `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}`.trim();
  return result.exitCode === 0;
}

// panel "Giriş yap": tek kullanımlık sihirli bağlantı alır, yeni sekmede açar (doğrudan wp-admin).
async function wpLogin() {
  if (!siteName.value || wpBusy.value) {
    return;
  }
  wpBusy.value = "login";
  wpNotice.value = "";
  try {
    await openAsyncReachable(async () => (await createWordPressLoginLink(siteName.value)).url);
    showWpNotice(t("panel.wpkit.loginOpened"), false);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel kurulu eklenti/tema yönetimi: tek bileşeni etkinleştir/devre dışı bırak/güncelle/sil (wp-cli), sonra listeyi tazele.
async function wpComponent(kind: "plugin" | "theme", action: "activate" | "deactivate" | "update" | "delete", slug: string) {
  if (!slug) {
    return;
  }
  if (action === "delete") {
    // Normal onay: tek bir eklenti/tema kaldırılır (yeniden kurulabilir) → ad yazdırmaya gerek yok.
    const approved = await confirm({
      title: t("panel.confirm.deleteWpComponentTitle", { name: slug }),
      message: t("panel.wpkit.confirmDelete", { name: slug }),
      impact: [`${t(kind === "plugin" ? "panel.wpkit.plugins" : "panel.wpkit.themes")}: ${slug}`],
    });
    if (!approved) {
      return;
    }
  }
  wpBusy.value = "comp";
  wpNotice.value = "";
  try {
    const ok = await runWp(`${kind} ${action} ${slug}`);
    showWpNotice(ok ? t("panel.wpkit.compDone") : t("panel.wpkit.failed"), !ok);
    if (ok) {
      await loadWp();
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel "Veri Kopyala": kaynağı VAR OLAN hedefe taşır (staging→canlı). YIKICI → önce onay; backend önce hedefi yedekler.
async function wpDoCopy() {
  if (!siteName.value || !wpCopy.targetSiteName || (!wpCopy.files && !wpCopy.database)) {
    return;
  }
  const impact: string[] = [`${t("panel.wpkit.copyTarget")}: ${wpCopy.targetSiteName}`];
  if (wpCopy.files) {
    impact.push(t("panel.wpkit.copyFiles"));
  }
  if (wpCopy.database) {
    impact.push(t("panel.wpkit.copyDatabase"));
  }

  // Yüksek riskli onay: hedef sitenin ÜZERİNE yazılır → hedef site adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.copyDataTitle", { name: wpCopy.targetSiteName }),
    message: t("panel.wpkit.copyConfirm", { target: wpCopy.targetSiteName }),
    impact,
    requireText: wpCopy.targetSiteName,
    confirmLabel: t("panel.wpkit.copyBtn"),
    busyLabel: t("panel.wpkit.working"),
    action: async () => {
      wpBusy.value = "copy";
      wpNotice.value = "";
      wpCopyResult.value = null;
      try {
        // Sunucunun KISMİ BAŞARI anahtarını göster, sabit "kopyalandı" metnini DEĞİL. Eskiden
        // `wp.copy_config_not_merged` / `wp.copy_url_not_replaced` sunucuda üretiliyor ama ekranda
        // hiç görünmüyordu — yani ölçülen uyarı kullanıcıya ulaşmıyordu.
        const kopya = await copyWordPressData(siteName.value, {
          targetSiteName: wpCopy.targetSiteName,
          files: wpCopy.files,
          database: wpCopy.database,
        });
        wpCopyResult.value = kopya.sonuc;
        const yarimKaldi = kopya.mesajAnahtari !== "" && kopya.mesajAnahtari !== "wp.copy_done";
        showWpNotice(
          yarimKaldi ? t(kopya.mesajAnahtari) : t("panel.wpkit.copyDone"),
          yarimKaldi);
      } catch (error) {
        showWpNotice(resolveApiMessage(error), true);
      } finally {
        wpBusy.value = "";
      }
    },
  });
}

// panel "Klonla": kaynak WP'yi yeni bir staging sitesine kopyalar (yalnız yönetici). Backend tüm işi orkestre eder.
async function wpDoClone() {
  if (!siteName.value || !wpClone.targetSiteName || !wpClone.targetHostName) {
    return;
  }
  wpBusy.value = "clone";
  wpNotice.value = "";
  wpCloneResult.value = null;
  try {
    // Kopyalamadaki ile aynı gerekçe: klon yarım kaldıysa (ör. wp-config yazılamadı, adres
    // değiştirilemedi) kullanıcı bunu GÖRMELİ — sabit "klonlandı" metni onu gizliyordu.
    const klon = await cloneWordPress(siteName.value, {
      targetSiteName: wpClone.targetSiteName,
      targetHostName: wpClone.targetHostName,
    });
    wpCloneResult.value = klon.sonuc;
    const klonYarim = klon.mesajAnahtari !== "" && klon.mesajAnahtari !== "wp.clone_done";
    showWpNotice(klonYarim ? t(klon.mesajAnahtari) : t("panel.wpkit.cloneDone"), klonYarim);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel "Otomatik güncellemeler": WP-native ayarlar (çekirdek sabiti + eklenti/tema auto-updates). Harici görev yok.
async function wpAutoUpdate(enable: boolean) {
  wpBusy.value = enable ? "autoOn" : "autoOff";
  wpNotice.value = "";
  wpOutput.value = "";
  try {
    const verb = enable ? "enable" : "disable";
    const commands = [
      `config set WP_AUTO_UPDATE_CORE ${enable ? "true" : "false"} --raw`,
      `plugin auto-updates ${verb} --all`,
      `theme auto-updates ${verb} --all`,
    ];
    let combined = "";
    let ok = true;
    for (const command of commands) {
      const result = await runWpCliCommand(siteName.value, command);
      combined += `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}\n\n`;
      if (result.exitCode !== 0) {
        ok = false;
      }
    }
    wpOutput.value = combined.trim();
    showWpNotice(
      ok ? t(enable ? "panel.wpkit.autoUpdatesEnabled" : "panel.wpkit.autoUpdatesDisabled") : t("panel.wpkit.failed"),
      !ok
    );
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

/**
 * "Güncelle": çekirdek + bütün eklentiler + bütün temalar.
 *
 * BAŞARISIZ GÜNCELLEME YEŞİL "Tamamlandı" DİYORDU. `exitCode` okunuyordu ama HİÇ BAKILMIYORDU:
 * üç komut da hata dönse bile satır `showWpNotice(t("panel.wpkit.updated"), false)` ile
 * BAŞARI olarak yazılıyordu. Kullanıcının güncellemenin yürümediğini anlamasının tek yolu, alt
 * taraftaki ham çıktı bloğunu okuyup yorumlamaktı — panelin cevabı ise "Güncelleme tamamlandı."
 * Bu, sitesini güncel sanan bir kullanıcı demektir; WordPress'te bunun bedeli güvenliktir.
 *
 * Doğru kalıp bu dosyada ZATEN VARDI: `wpAutoUpdate` aynı üçlü döngüyü kurup `exitCode`a bakıyor
 * ve `ok`u aşağı taşıyor. Kopyalanan o kalıptır; komutlar, sıraları ve çıktı DEĞİŞMEDİ.
 */
async function wpUpdate() {
  wpBusy.value = "update";
  wpNotice.value = "";
  wpOutput.value = "";
  try {
    let combined = "";
    let ok = true;
    for (const command of ["core update", "plugin update --all", "theme update --all"]) {
      const result = await runWpCliCommand(siteName.value, command);
      combined += `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}\n\n`;
      if (result.exitCode !== 0) {
        ok = false;
      }
    }
    wpOutput.value = combined.trim();
    showWpNotice(ok ? t("panel.wpkit.updated") : t("panel.wpkit.failed"), !ok);
    // Liste BAŞARISIZ turdan sonra da tazelenir: bazı eklentiler geçmiş, bazıları geçmemiş
    // olabilir ve kullanıcının göreceği sürümler gerçek durumu göstermelidir.
    await loadWp();
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

/**
 * Sunucunun uyguladığı güvenlik önleminin KODU → panelin METNİ.
 *
 * HAM ANAHTAR EKRANDAYDI — hem de her seferinde. Şablon `t("panel.wpkit.sec_" + code)` çağırıyordu
 * ama sözlükte `panel.wpkit.sec_*` diye TEK BİR ANAHTAR YOKTU (tr/en ikisinde de sıfır). Yani
 * "Güvenlik" düğmesine basan herkes çıktı kutusunda şunu görüyordu:
 *   ✓ panel.wpkit.sec_hide_version
 *   ✓ panel.wpkit.sec_xmlrpc_pingback   ... (beş satır, beşi de kod)
 * Kodlar `WordPressToolkitService`ten SAYILARAK alındı: hide_version, xmlrpc_pingback,
 * author_enum, file_edit, salts.
 *
 * ANAHTARLAR NEDEN ELLE EŞLENİYOR (dize birleştirmesi değil): (1) sözlükteki anahtar kaynakta
 * ARANABİLİR olur — kullanılmayan çeviri nöbetçisi dizeyi görebilsin diye, (2) sunucu yeni bir
 * önlem eklediğinde ekrana yine ham `panel.wpkit.sec_...` basılmaz, aşağıdaki yedek yol devreye
 * girer.
 */
const HARDEN_ETIKET_ANAHTARLARI: Record<string, string> = {
  hide_version: "panel.wpkit.sec_hide_version",
  xmlrpc_pingback: "panel.wpkit.sec_xmlrpc_pingback",
  author_enum: "panel.wpkit.sec_author_enum",
  file_edit: "panel.wpkit.sec_file_edit",
  salts: "panel.wpkit.sec_salts"
};

/** Tanınmayan kodda SUNUCUNUN KODU yazılır; bir çeviri anahtarı asla yazılmaz. */
function hardenEtiketi(code: string): string {
  const anahtar = HARDEN_ETIKET_ANAHTARLARI[code];
  if (!anahtar) {
    return code;
  }

  const cevrilen = t(anahtar);
  return cevrilen === anahtar ? code : cevrilen;
}

// panel "Güvenlik": önlem demetini backend'de uygular, uygulanan önlemleri etiketleyip listeler.
async function wpHarden() {
  if (!siteName.value) {
    return;
  }
  wpBusy.value = "harden";
  wpNotice.value = "";
  try {
    const result = await hardenWordPress(siteName.value);
    wpOutput.value = result.applied.map((code) => `✓ ${hardenEtiketi(code)}`).join("\n");
    // HİÇBİR ÖNLEM UYGULANMADIYSA "uygulandı" DENMEZ. Sunucu wp-config'e yazamadığında
    // (dosya salt okunur, izin yok) boş bir liste dönebiliyor; eski hâlde ekran yine yeşil
    // "Güvenlik uygulandı" diyor ve altındaki çıktı kutusu BOMBOŞ kalıyordu.
    showWpNotice(
      result.applied.length > 0 ? t("panel.wpkit.hardened") : t("panel.wpkit.hardenedNone"),
      result.applied.length === 0);
    // Uygulanan önlemler yukarıdaki "Güvenlik durumu" listesini değiştirir; tazelenmezse
    // kullanıcı az önce kapattığı ayarı hâlâ "Kapalı" görürdü.
    await loadWp();
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpToggleMaintenance() {
  if (!wp.value) {
    return;
  }
  wpBusy.value = "maint";
  wpNotice.value = "";
  try {
    wp.value = await setWordPressMaintenance(siteName.value, !wp.value.maintenanceMode);
    showWpNotice(t("panel.wpkit.maintenanceUpdated"), false);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpInstallPlugin() {
  if (!wpForm.pluginSlug) {
    return;
  }
  wpBusy.value = "plugin";
  wpNotice.value = "";
  try {
    const ok = await runWp(`plugin install ${wpForm.pluginSlug} --activate`);
    showWpNotice(ok ? t("panel.wpkit.installed") : t("panel.wpkit.failed"), !ok);
    if (ok) {
      wpForm.pluginSlug = "";
      await loadWp();
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpSearchReplace() {
  if (!wpForm.srFrom || !wpForm.srTo) {
    return;
  }
  wpBusy.value = "sr";
  wpNotice.value = "";
  try {
    const ok = await runWp(`search-replace ${wpForm.srFrom} ${wpForm.srTo} --all-tables --report-changes-only`);
    showWpNotice(ok ? t("panel.wpkit.replaced") : t("panel.wpkit.failed"), !ok);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

function genPassword(): string {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return "Wp!" + Array.from(bytes).map((value) => value.toString(16).padStart(2, "0")).join("");
}

/**
 * Parola sıfırlama ONAYSIZ YAPILMAZ.
 *
 * Denetimde bulundu: tek tık, müşterinin WordPress yönetici parolasını değiştiriyordu ve geri
 * dönüşü YOKTU — eski parola artık bilinmiyor. Yanlış tıklayan kullanıcı, kendi sitesine giriş
 * bilgisini kaybetmiş oluyor; parolayı kullanan eklenti ya da betik varsa onlar da düşüyor.
 * Bu ekrandaki diğer yıkıcı işlemler zaten onay istiyordu, bu atlanmıştı.
 */
async function wpResetPassword() {
  const user = (wpForm.adminUser || "admin").trim();
  if (!user) {
    return;
  }

  await confirm({
    title: t("panel.wpkit.resetPassword"),
    message: t("panel.wpkit.confirmResetPassword"),
    action: wpResetPasswordUygula
  });
}

async function wpResetPasswordUygula() {
  const user = (wpForm.adminUser || "admin").trim();
  wpBusy.value = "pass";
  wpNotice.value = "";
  wpNewPassword.value = "";
  try {
    const password = genPassword();
    const ok = await runWp(`user update ${user} --user_pass=${password} --skip-email`);
    if (ok) {
      wpNewPassword.value = password;
      showWpNotice(t("panel.wpkit.passwordReset"), false);
    } else {
      showWpNotice(t("panel.wpkit.failed"), true);
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

const queueRunning = ref(false);
const queueBusy = ref(false);
const queueLoaded = ref(false);
// Durum OKUNAMADIYSA "Durduruldu" DEMEYİZ: çalışan bir işçiyi durmuş göstermek yanlış bilgidir.
const queueLoad = useSectionError();
const queueNotice = ref("");
const queueNoticeError = ref(false);
const schedulerRunning = ref(false);
const schedulerLoaded = ref(false);
const schedulerLoad = useSectionError();
const schedulerBusy = ref(false);

function selectQueue() {
  tab.value = "queue";
  if (!queueLoaded.value) {
    void loadQueue();
    void loadScheduler();
  }
}

async function loadQueue() {
  if (!siteName.value) {
    return;
  }
  try {
    const status = await getQueueWorker(siteName.value);
    queueRunning.value = status.running;
    queueLoaded.value = true;
    queueLoad.clear();
  } catch (error) {
    // Sessiz geçilmiyordu-geçiliyordu: durum okunamadığında ekran "Durduruldu" gösteriyor,
    // kullanıcı çalışan işçiyi durmuş sanıp ikinci bir tane başlatabiliyordu.
    queueLoaded.value = false;
    queueLoad.capture(error);
  }
}

async function startQueue() {
  queueBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await startQueueWorker(siteName.value);
    queueRunning.value = status.running;
    queueLoaded.value = true;
    showQueueNotice(t("panel.toolkit.queue.started"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    queueBusy.value = false;
  }
}

async function stopQueue() {
  queueBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await stopQueueWorker(siteName.value);
    queueRunning.value = status.running;
    queueLoaded.value = true;
    showQueueNotice(t("panel.toolkit.queue.stopped"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    queueBusy.value = false;
  }
}

async function loadScheduler() {
  if (!siteName.value) {
    return;
  }
  try {
    const status = await getLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
    schedulerLoaded.value = true;
    schedulerLoad.clear();
  } catch (error) {
    schedulerLoaded.value = false;
    schedulerLoad.capture(error);
  }
}

async function startScheduler() {
  schedulerBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await startLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
    schedulerLoaded.value = true;
    showQueueNotice(t("panel.toolkit.scheduler.started"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    schedulerBusy.value = false;
  }
}

async function stopScheduler() {
  schedulerBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await stopLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
    schedulerLoaded.value = true;
    showQueueNotice(t("panel.toolkit.scheduler.stopped"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    schedulerBusy.value = false;
  }
}

function showQueueNotice(message: string, isError: boolean) {
  queueNotice.value = message;
  queueNoticeError.value = isError;
}
</script>

<style scoped>
.toolkit {
  display: grid;
  gap: var(--sp-12);
}

/* İki servis kutusu arasındaki dikey ritim, panelin kendi ızgara aralığıyla AYNI. */
.queue-stack {
  display: grid;
  gap: var(--sp-12);
}

/* YIKICI EYLEMİN SABİT YERİ: satırın sağ ucu (aynı ad ve aynı tek satırlık kural SecurityView'de
   de var). `.wp-row` bir flex satırı olduğu için `auto` kenar boşluğu arta kalan yeri yutar ve
   düğme her panel genişliğinde AYNI yerde durur.
   TANIM NEDEN KOPYA: `<style scoped>` bileşenler arası GEÇMEZ, dolayısıyla iki ekranın da kendi
   tanımı olmak zorunda (nöbetçi: CssSharedClassTests). Doğru ev `styles/base.css`tir; o dosya
   bu turun kapsamı dışında olduğu için kopya BİLEREK bırakıldı. */
.destructive-slot {
  margin-left: auto;
}

/* `queue-panel` / `.pill` / `.pill.stopped` / `.state-dot` / `.sched-head` KOPYALARI SİLİNDİ:
   ilki `form-surface`in (base.css) elle yazılmış ikizi, ortadaki üçü panelin ortak
   `status-pill` rozetinin üçüncü ayrı sürümü, sonuncusu ise iki servisi tek kutuda tutan
   1px'lik ayraçtı. Görünen etkileri şablondaki yorumlarda tek tek yazılı. */
.wp-panel {
  /* `.form-surface` ile birlikte kullanılıyor; buradan YALNIZCA aralık geliyor. WordPress
     sekmesindeki bölümler kendi üst çizgilerini taşıdığı için (bkz. `.wp-form`) ortak 12px
     aralık burada 14'e çıkarılmıştı; ölçeğe (16) taşındı ki bölüm çizgisi ile üstündeki
     içerik arasındaki nefes payı ölçekten okunsun. */
  gap: var(--sp-16);
}
.wp-status {
  display: flex;
  align-items: center;
  gap: var(--sp-10);
  flex-wrap: wrap;
}
/* 0.85rem ölçek dışıydı. `--fs-sm` (0.86rem) en yakın adım; fark 0.135px, hiçbir ekranda
   ayırt edilemez. */
.wp-admin-link {
  margin-left: auto;
  font-size: var(--fs-sm);
  font-weight: 600;
}
.wp-actions {
  display: flex;
  gap: var(--sp-8);
  flex-wrap: wrap;
}
.wp-form {
  display: grid;
  gap: var(--sp-6);
  padding-top: var(--sp-12);
  border-top: 1px solid var(--panel-border);
}
.wp-row {
  display: flex;
  gap: var(--sp-8);
  flex-wrap: wrap;
  align-items: center;
}
/* Bu kural GEREKLİ: base.css'te `input { width: 100% }` var ve o olmadan her metin alanı
   satırın tamamını kaplayıp yanındaki düğmeyi alt satıra düşürüyor. */
.wp-row input {
  flex: 1 1 160px;
  width: auto;
}
.wp-row button {
  flex: none;
}
/* `--font-mono` base.css'te TANIMLI; buradaki iki yedek liste ELDE yazılıydı ve panelin geri
   kalanındakinden FARKLIYDI ("Cascadia Mono" ve "Liberation Mono" eksikti) — aynı ekranda
   birbirinden farklı iki tek-aralıklı yazı tipi çizilebiliyordu.
   Renk de düzeltildi: ÖLÇÜLDÜ, açık temada `--accent-1` (#1474e5) kendi %16'lık tonuyla
   karışan zeminde (#d3e5fb) 3.29:1 veriyordu — AA eşiği 4.5. `--accent-text` (#0f62c4) aynı
   zeminde 4.53:1. Burada gösterilen şey bir PAROLA ya da veritabanı adı; panelde okunması
   en kritik metin tam olarak budur. */
.wp-cred code {
  font-family: var(--font-mono);
  background: color-mix(in srgb, var(--accent-1) 16%, transparent);
  color: var(--accent-text);
  padding: var(--sp-2) var(--sp-8);
  border-radius: var(--radius-sm);
  user-select: all;
}
.wp-output {
  margin: 0;
  padding: var(--sp-12);
  border-radius: var(--radius-md);
  background: var(--surface-0);
  border: 1px solid var(--panel-border);
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 280px;
  overflow: auto;
}
.wp-list {
  display: grid;
  gap: var(--sp-4);
  max-height: 220px;
  overflow: auto;
}
.wp-list-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-8);
  padding: var(--sp-6) var(--sp-8);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--panel-border) 30%, transparent);
}
.wp-list-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
/* `opacity: 0.6` KALDIRILDI: sürüm numarası zaten `--text-strong` üzerine %60 saydamlıkla
   çiziliyordu ve bu, ölçülebilir bir kontrast kaybıdır (koyu temada 15.8:1 -> 6.4:1, açık
   temada 14.9:1 -> 5.6:1) ama hiçbir HİYERARŞİ kurmuyordu, çünkü boyut aynı kalıyordu.
   İkincil bilgi panelin kendi ikincil metin belirteciyle söylenir. */
.wp-list-name small {
  color: var(--text-muted);
}
.wp-list-actions {
  display: flex;
  align-items: center;
  gap: var(--sp-6);
  flex: none;
}
.wp-check {
  display: flex;
  align-items: center;
  gap: var(--sp-4);
  white-space: nowrap;
  flex: none;
}
.wp-check input {
  width: auto;
  flex: none;
}
</style>
