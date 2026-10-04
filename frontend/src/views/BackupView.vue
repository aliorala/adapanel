<template>
  <section class="panel module-shell backup-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.backup.title") }}</h3>
        <p class="meta">{{ t(isAdmin ? "panel.backup.subtitle" : "panel.backup.scopedSubtitle") }}</p>
      </div>
      <div class="row-actions">
        <!-- "Yedekleri doğrula": listedeki kayıtların GERÇEKTEN durup durmadığını ölçer.
             Ayrı bir düğme çünkü uzak ölçüm kayıt başına bir AĞ isteğidir; her sayfa açılışında
             yapılsaydı panel yavaşlar ve hedefin hız sınırına takılırdı. Hiçbir şeyi SİLMEZ. -->
        <button class="secondary compact" :disabled="loading || verifying" @click="verify">
          {{ verifying ? t("panel.backup.verify.running") : t("panel.backup.verify.button") }}
        </button>
        <button class="secondary compact" :disabled="loading" @click="refresh">
          {{ t("panel.backup.refresh") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
      <button v-if="listErrorInNotice || schedulesErrorInNotice" type="button" class="secondary compact" :disabled="busy || loading || schedulesLoading" @click="retryFailedLoads">
        {{ t("panel.common.retry") }}
      </button>
    </p>

    <BackupRestoreResult v-if="fullRestore.result.value" :result="fullRestore.result.value" :refresh-error="fullRestore.refreshError.value" />

    <!-- DOĞRULAMA SONUCU. Üç sayı ayrı ayrı gösterilir; "ulaşılamadı" KAYIP DEĞİLDİR ve
         karıştırılırsa kullanıcı geçici bir ağ arızasında bütün geçmişini kayıp sanar. -->
    <section v-if="verification" class="form-surface verify-panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.verify.title") }}</h3>
          <p class="meta">{{ t("panel.backup.verify.hint") }}</p>
        </div>
        <button class="secondary compact" @click="verification = null">{{ t("panel.backup.verify.close") }}</button>
      </div>

      <span class="pill-row">
        <span class="status-pill">{{ t("panel.backup.verify.checked", { count: verification.checked }) }}</span>
        <span :class="['status-pill', verification.missingLocal ? 'danger' : '']">
          {{ t("panel.backup.verify.missingLocal", { count: verification.missingLocal }) }}
        </span>
        <span v-if="verification.previouslyDeletedLocal" class="status-pill">
          {{ t("panel.backup.verify.previouslyDeletedLocal", { count: verification.previouslyDeletedLocal }) }}
        </span>
        <span :class="['status-pill', verification.missingRemote ? 'danger' : '']">
          {{ t("panel.backup.verify.missingRemote", { count: verification.missingRemote }) }}
        </span>
        <!-- "Dosya var ama içi yok" AYRI sayılır: kayıp dosyayla aynı kefeye konsaydı,
             doğrulamanın ölçtüğü tek yeni gerçek görünmez olurdu. -->
        <span :class="['status-pill', verification.unsound ? 'danger' : '']">
          {{ t("panel.backup.verify.unsound", { count: verification.unsound }) }}
        </span>
        <span :class="['status-pill', verification.unreachable ? 'warn' : '']">
          {{ t("panel.backup.verify.unreachable", { count: verification.unreachable }) }}
        </span>
      </span>

      <p v-if="verification.unreachable" class="notice" role="status">
        {{ t("panel.backup.verify.unreachableNote") }}
      </p>

      <ul v-if="verification.issues.length" class="verify-list">
        <li v-for="issue in verification.issues" :key="`${issue.backupId}-${issue.messageKey}`">
          <strong>{{ issue.name }}</strong>
          <span>{{ t(issue.messageKey) }}</span>
          <small v-if="issue.detail">{{ issue.detail }}</small>
        </li>
      </ul>

      <!-- TERS YETİM: hedefte duran ama panelde kaydı olmayan dosyalar. Yalnız LİSTELENİR;
           otomatik silmek, kullanıcının aynı klasördeki başka dosyalarını yok edebilirdi. -->
      <template v-if="verification.orphans.length">
        <h4>{{ t("panel.backup.verify.orphansTitle") }}</h4>
        <p class="field-hint">{{ t("panel.backup.verify.orphansHint") }}</p>
        <ul class="verify-list">
          <li v-for="orphan in verification.orphans" :key="orphan.remotePath">
            <code class="path-box">{{ orphan.remotePath }}</code>
            <small>{{ orphan.targetType }}</small>
          </li>
        </ul>
      </template>

      <!-- Boş yetim listesi "yetim yok" DEMEK DEĞİLDİR: taranamayan hedefi söylemek şart. -->
      <p v-if="verification.orphanScanSkipped.length" class="notice warn" role="status">
        {{ t("panel.backup.verify.orphanScanSkipped", { targets: verification.orphanScanSkipped.join(", ") }) }}
      </p>
    </section>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeView === 'list'" :class="{ active: activeView === 'list' }" @click="activeView = 'list'">
          {{ t("panel.backup.list") }}
        </button>
        <button role="tab" :aria-selected="activeView === 'create'" :class="{ active: activeView === 'create' }" @click="activeView = 'create'">
          {{ t("panel.backup.new") }}
        </button>
        <button role="tab" :aria-selected="activeView === 'schedules'" :class="{ active: activeView === 'schedules' }" @click="openSchedules">
          {{ t("panel.backup.schedules") }}
        </button>
        <!--
          AYARLAR SEKMESİ — liste sekmesindeki DUVARI kaldırmak için.

          ÖLÇÜLDÜ: "Liste" sekmesinde aynı anda beş büyük blok çiziliyordu (tablo + sayfalayıcı +
          otomatik yedek kartı + şifreleme kartı + tam boy "yeni yedek" formu). İlk ikisi
          "neyim var", son üçü ise ayar/eylem; kullanıcı listesini görmek için iki ayar kartını
          aşağı doğru geçmek zorundaydı.

          HİÇBİR ŞEY GİZLENMEDİ, TAŞINDI: iki ayar kartı bir tık ötede, zaten var olan sekme
          şeridinde duruyor (Programlar da öyle). Veri de gizlenmiyor — `onMounted` her iki ayarı
          sekmeden BAĞIMSIZ olarak yüklüyor, yani sekme yalnızca çizimi ayırır.

          YALNIZ YÖNETİCİ: iki kart da `isAdmin` kapılı; sekmeyi herkese göstermek müşteriye
          BOŞ bir sekme açmak olurdu.
        -->
        <button
          v-if="isAdmin"
          role="tab"
          :aria-selected="activeView === 'settings'"
          :class="{ active: activeView === 'settings' }"
          @click="activeView = 'settings'">
          {{ t("panel.backup.settingsTab") }}
        </button>
      </div>

      <!-- Arama TÜM listede çalışır (gün grupları ve sayfalama ÜSTÜNDE): kapalı bir günün ya da
           başka bir sayfanın içindeki yedeği kaçırmayalım. Sayfalamadan ÖNCE uygulanır. -->
      <label v-if="activeView === 'list'" class="toolbar-search">
        <span class="meta">{{ t("panel.backup.search") }}</span>
        <input v-model.trim="search" type="search" :placeholder="t('panel.backup.searchPlaceholder')" />
      </label>

      <span v-if="activeView === 'list'" class="status-pill">
        {{ !listConfirmed ? (loading ? "…" : "—") : search
          ? t("panel.backup.recordsFiltered", { shown: filteredBackups.length, total: backups.length })
          : `${backups.length} ${t("panel.backup.records")}` }}
      </span>
    </div>

    <!-- Yedek ALINIRKEN ekran donmuş görünmesin: uç işi arka plana almıyor, istek uçuştayken
         yedek GERÇEKTEN sürüyor (bkz. composables/useRunningTask.ts).

         ŞERİT İKİ KAYNAKTAN BESLENİR: bu sekmede başlatılan iş (runningBackup) ve SUNUCUDA süren
         iş (/backup/running). İkincisi sayfa yenilendikten sonra da doludur; ayrıca panel iş
         sürerken yeniden başlamışsa "yarıda kaldı" uyarısı buradan çıkar. -->
    <RunningTaskBanner
      :running="bannerRunning"
      :label="bannerLabel"
      :elapsed-text="bannerElapsedText"
      :hint="bannerHint"
      :interrupted-label="serverBackup.interruptedText.value"
      @dismiss="serverBackup.dismissInterrupted" />

    <!--
      YEDEK KÖK KLASÖRÜ TABLONUN DIŞINA ALINDI.

      ÖLÇÜLDÜ: bu satır (ayar alanı + Kopyala düğmesi + iki cümlelik yardım metni) `data-table`
      DIV'inin İÇİNDE, başlık satırı ile "Henüz yedek bulunmuyor." arasında duruyordu. Yani
      tablonun sütun düzeninin ortasında, hiçbir yedeğe ait olmayan bir ayar satırı vardı;
      liste boşken de tablo "doluymuş" gibi görünüyordu.

      Kök klasör bütün listenin BAĞLAMIDIR, bir satırı değil: artık tablonun üstünde, kendi
      şeridinde. Değer Ayarlar'daki `defaultBackupRoot` tercihinden OKUNUR (uydurulmaz); uç
      yalnız yöneticiye açık olduğundan okunamadığında şerit HİÇ çizilmez.
    -->
    <p v-if="activeView === 'list' && backupRoot" class="location-root">
      <span class="field-label">{{ t("panel.backup.location.root") }}</span>
      <code class="path-box">{{ backupRoot }}</code>
      <button type="button" class="secondary compact" @click="copyPath(backupRoot)">
        {{ t("panel.backup.location.copy") }}
      </button>
      <small class="cell-muted">{{ t("panel.backup.location.rootHint") }}</small>
    </p>

    <div v-if="activeView === 'list'" class="data-table backup-table">
      <!-- BEŞ sütun. Önceden sekizdi ve dördü (tür, hedef türü, boyut, tarih) tek kelimelik
           değerler için tam sütun harcıyordu; ad, yol ve dört düğme aynı satırda sıkışıyordu.
           Birbirine ait olanlar tek hücrede: kaynağın türü kaynağın parçası, boyut hedefin
           parçası, oluşturma zamanı durumun parçası. -->
      <div class="data-row table-head backup-row">
        <span>{{ t("panel.backup.name") }}</span>
        <span>{{ t("panel.backup.source") }}</span>
        <span>{{ t("panel.backup.targetType") }}</span>
        <span>{{ t("panel.backup.statusColumn") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <!--
        GÜN GRUPLARI. Gruplama `createdAtUtc`ten KULLANICININ YEREL gününe çevrilerek yapılır
        (bkz. composables/useDayGroups.ts); dosya YOLU ayrıştırılmaz — gün klasörü olmadan
        alınmış ESKİ yedekler de bu sayede doğru günün altında durur.

        SAYFALAMA KAYITLARA uygulanır, günlere değil (gerekçe: `pageGroups` yorumunda).
        Gün başlığındaki sayı/boyut özeti ise SÜZÜLMÜŞ LİSTENİN TAMAMINDAN ölçülür, açık
        sayfadan değil — yoksa "3 yedek" yazan bir gün başlığı aslında 40 yedeği olan bir günü
        anlatıyor olurdu.
      -->
      <template v-for="group in pageGroups" :key="group.key">
        <div class="day-group-head">
          <button
            type="button"
            class="day-toggle"
            :aria-expanded="isDayOpen(group.key)"
            @click="toggleDay(group.key)">
            <span class="day-caret" aria-hidden="true">{{ isDayOpen(group.key) ? "▾" : "▸" }}</span>
            <strong>{{ group.label }}</strong>
          </button>
          <span class="pill-row day-summary">
            <span class="status-pill">{{ t("panel.backup.day.count", { count: dayStats(group.key).count }) }}</span>
            <span class="status-pill">{{ formatBytes(dayStats(group.key).size) }}</span>
            <!-- Gün, sayfa sınırında BÖLÜNDÜ: kullanıcı geri kalanının kaybolmadığını bilsin. -->
            <small v-if="group.items.length < dayStats(group.key).count" class="cell-muted">
              {{ t("panel.backup.day.partial", { shown: group.items.length, total: dayStats(group.key).count }) }}
            </small>
          </span>
        </div>
        <template v-for="backup in (isDayOpen(group.key) ? group.items : [])" :key="backup.id">
          <!--
            AÇIK AYRINTI = SATIRIN KENDİSİ İŞARETLENİR (üçüncü katman).

            Ad ve sol çubuk belirsizliği kaldırdı ama satır ile kutu hâlâ İKİ ayrı blok gibi
            duruyordu. Burada satır, ayrıntısı açıkken kutuyla AYNI zemini alır ve aradaki alt
            kenarlık kaldırılır: ikisi tek bir blok olarak okunur. Bağ artık yalnız metinle değil
            YÜZEYLE de kuruluyor; kutu hangi satıra ait, göz seviyesinde görünür.
          -->
          <div
            class="data-row backup-row"
            :class="{ 'row-expanded': expandedWarnings === backup.id || expandedLocation === backup.id }">
            <div class="cell-main">
              <strong>{{ backup.name }}</strong>
              <span class="pill-row">
                <!-- Zincirdeki YER: artımsal halka mı, zincirin tam yedeği mi. Zinciri olmayan
                     tekil yedekte rozet YOKTUR (yanlış bir bağ hissi vermesin). -->
                <span v-if="backup.chainId && backup.backupMode === 'incremental'" class="status-pill chain-link">
                  {{ t("panel.backup.chain.linkBadge", { index: backup.chainIndex }) }}
                </span>
                <span v-else-if="backup.chainId" class="status-pill chain-head">
                  {{ t("panel.backup.chain.headBadge") }}
                </span>
                <span v-if="backup.itemCount > 0" class="status-pill">
                  {{ t("panel.backup.itemCount", { count: backup.itemCount }) }}
                </span>
              </span>
              <small>{{ backup.id }}</small>
            </div>
            <div class="cell-main">
              <strong>{{ backupSourceLabel(backup) }}</strong>
              <small>{{ backupKindLabel(backup.type) }}</small>
            </div>
            <div class="cell-main">
              <strong>{{ backup.remoteUri ? t("panel.backup.remoteTarget") : t("panel.backup.localTarget") }}</strong>
              <small>{{ formatBytes(backup.size) }}</small>
              <!-- Yedeğin DİSKTEKİ yeri. Satırın içine basılmaz: tam yol satırı bozacak kadar
                   uzundur ve her satırda yer kaplaması listeyi okunmaz yapardı. -->
              <button
                type="button"
                class="secondary compact location-toggle"
                :aria-expanded="expandedLocation === backup.id"
                @click="toggleLocation(backup.id)">
                {{ t("panel.backup.location.toggle") }}
              </button>
            </div>
            <div class="cell-main">
              <span class="pill-row">
                <span :class="['status-pill', statusTone(backup.status)]">{{ formatStatus(backup.status) }}</span>
                <!-- DOSYASI YOK rozeti. Kayıt listede durup dosyası olmayan bir yedeğin
                     "geri yüklenebilir" görünmesi, kullanıcının bunu ancak felaket anında
                     öğrenmesi demekti. Yerel varlık her listelemede ölçülüyor; rozet o ölçümün
                     görünen yüzü. -->
                <span
                  v-if="backup.localMissing"
                  class="status-pill danger"
                  :title="t('panel.backup.missingLocalHint')">
                  {{ t("panel.backup.missingLocalBadge") }}
                </span>
                <!-- Uzak kopya YALNIZCA doğrulama çalıştıysa ölçülmüştür; ölçülmemiş bir kaydı
                     "uzakta yok" göstermek yalan olurdu, bu yüzden `remoteVerifiedUtc` şart. -->
                <span
                  v-if="backup.remoteMissing && backup.remoteVerifiedUtc"
                  class="status-pill danger"
                  :title="t('panel.backup.missingRemoteHint')">
                  {{ t("panel.backup.missingRemoteBadge") }}
                </span>
                <!-- Kilit rozeti: geri yüklerken parola isteneceğini kullanıcı LİSTEDE görsün. -->
                <span v-if="backup.encrypted" class="status-pill" :title="t('panel.backup.encryptedHint')">
                  {{ t("panel.backup.encryptedBadge") }}
                </span>
                <!-- Yarım kalan/riskli adımlar sessizce yutulmaz: rozet TIKLANIR ve sebepler açılır
                     (ipucu metni dokunmatik ekranda hiç görünmüyordu). -->
                <button
                  v-if="backup.warnings?.length"
                  type="button"
                  class="status-pill warn warning-toggle"
                  :aria-expanded="expandedWarnings === backup.id"
                  @click="toggleWarnings(backup.id)">
                  {{ t("panel.backup.warningsBadge", { count: backup.warnings.length }) }}
                </button>
              </span>
              <small>{{ new Date(backup.createdAtUtc).toLocaleString(getFormattingLocale()) }}</small>
            </div>
            <!--
              YIKICI EYLEM AYRI SATIRDA.

              SAYILDI: bu hücrede en çok BEŞ düğme var ve sütun 320px — dördü sığmadığı için
              sarıyor. Sarma sırası kayıtla değişiyordu (zinciri/zip'i olmayan yedekte düğme
              sayısı 3'e düşer), yani "Sil" bir satırda "İndir"in yanında, bir sonrakinde
              "Geri yükle"nin altında çıkıyordu: yıkıcı düğmenin yeri her satırda BAŞKAydı.
              Güvenli dördü bir kutuya alındı; kutu tam genişlik olduğu için "Sil" HER ZAMAN
              kendi satırında ve sağda durur. Düğme sayısı, etiketi, kapısı DEĞİŞMEDİ.
            -->
            <div class="row-actions">
              <span class="safe-actions">
                <button v-if="isAdmin" class="secondary compact row-button" v-bind="writeGate(busy)" @click="restore(backup)">
                  {{ t("panel.backup.restore") }}
                </button>
                <button
                  v-if="backup.format === 'zip'"
                  class="secondary compact row-button"
                  :disabled="busy"
                  @click="openBrowser(backup)">
                  {{ t("panel.backup.browse") }}
                </button>
                <button
                  v-if="backup.chainId"
                  class="secondary compact row-button"
                  :disabled="busy"
                  @click="openChain(backup)">
                  {{ t("panel.backup.chain.view") }}
                </button>
                <button class="secondary compact row-button" :disabled="busy" @click="download(backup)">
                  {{ t("panel.backup.download") }}
                </button>
              </span>
              <template v-if="isAdmin && backup.status === 'created_remote_failed' && backup.targetType === 'ftp' && !backup.remoteUri && backup.type !== 'subscription' && backup.path && !backup.localMissing">
                <button v-if="backup.contentHash?.trim()"
                  class="secondary compact row-button" v-bind="writeGate(busy)" @click="retryRemote(backup)">
                  {{ t("panel.backup.retryRemote") }}
                </button>
                <small v-else class="cell-muted retry-unavailable">{{ t("panel.backup.retryMissingHash") }}</small>
              </template>
              <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeBackup(backup)">
                {{ t("panel.backup.delete") }}
              </button>
            </div>
          </div>
          <!--
            AYRINTI KUTUSU HANGİ YEDEĞE AİT — BAŞLIKTA ADIYLA YAZAR.

            Kullanıcı bildirdi: "uyarıya basıyorum, alt satırdaki yedeğin uyarısı gibi görünüyor."
            Ölçüldü ve haklıydı: kutu satırın KAPANIŞINDAN SONRA, kardeş öge olarak çiziliyor,
            yani N. satır ile N+1. satırın ARASINA düşüyor ve alttakinin başlığı gibi okunuyor.
            Kimlik bağı (`expandedWarnings === backup.id`) doğruydu; yanlış olan GÖRSEL bağdı.

            İki katmanlı düzeltildi: (1) başlık yedeğin ADINI yazar — piksel yerleşimi ne olursa
            olsun belirsizlik kalmaz, (2) kutu sola girintili ve sol kenarından renkli bir çubukla
            YUKARI bağlanır. Yalnız görsele güvenmek yetmezdi: dar ekranda satırlar sarınca
            girinti ipucu zayıflar, ad ise her zaman okunur.
          -->
          <div v-if="expandedWarnings === backup.id" class="warning-detail">
            <strong>{{ t("panel.backup.warningsListTitle", { name: backup.name }) }}</strong>
            <ul>
              <li v-for="(warning, index) in backup.warnings" :key="index">
                {{ warningLine(warning) }}
                <small v-if="warning.detail">{{ warning.detail }}</small>
              </li>
            </ul>
          </div>

          <!-- YEDEĞİN YERİ. Kayıt yolu OLDUĞU GİBİ gösterilir (kısaltılmaz): kullanıcı bu yolu
               kopyalayıp Dosya Gezgini'ne/FTP'ye yapıştıracak. Uzun yol satırı bozmasın diye
               kutu KENDİ İÇİNDE yatay kayar. -->
          <div v-if="expandedLocation === backup.id" class="location-detail">
            <!-- Uyarı kutusuyla AYNI belirsizlik burada da vardı; aynı çözüm uygulandı. -->
            <strong class="detail-title">{{ t("panel.backup.locationTitle", { name: backup.name }) }}</strong>
            <div class="location-line">
              <span class="field-label">{{ t("panel.backup.location.local") }}</span>
              <code v-if="backup.path" class="path-box">{{ backup.path }}</code>
              <span v-else class="cell-muted">{{ t(isAdmin ? "panel.backup.location.unknown" : "panel.backup.location.scopedHidden") }}</span>
              <button
                v-if="backup.path"
                type="button"
                class="secondary compact"
                @click="copyPath(backup.path)">
                {{ t("panel.backup.location.copy") }}
              </button>
            </div>

            <!-- UZAK kopya. Adres KİMLİKSİZ basılır: sunucu `user:pass@` kısmını arındırıyor,
                 biz ek olarak SORGU DİZESİNİ de düşürüyoruz (S3 "presigned" hedefinde imza
                 orada durur ve ekran görüntüsüyle paylaşılırsa hedef depo devredilmiş olur). -->
            <div v-if="backup.remoteUri" class="location-line">
              <span class="field-label">{{ t("panel.backup.location.remote") }}</span>
              <code v-if="safeRemote(backup.remoteUri).text" class="path-box">{{ safeRemote(backup.remoteUri).text }}</code>
              <span v-else class="cell-muted">{{ t("panel.backup.location.remoteHidden") }}</span>
              <button
                v-if="safeRemote(backup.remoteUri).text"
                type="button"
                class="secondary compact"
                @click="copyPath(safeRemote(backup.remoteUri).text)">
                {{ t("panel.backup.location.copy") }}
              </button>
            </div>
            <p v-if="backup.remoteUri && safeRemote(backup.remoteUri).masked" class="field-hint">
              {{ t("panel.backup.location.remoteMasked") }}
            </p>

            <!-- Kayıt, dosyanın diskte HÂLÂ DURUP DURMADIĞINI taşımıyor: uç bunu ölçmüyor.
                 "Dosya yerinde" demek ölçmediğimiz şeye güvenmek olurdu; onun yerine ne
                 bilmediğimizi söylüyoruz. -->
            <p class="field-hint">{{ t("panel.backup.location.existenceUnknown") }}</p>
          </div>
        </template>
      </template>

      <!-- "Yükleniyor" / "kayıt yok" / "okunamadı" ÜÇÜ AYRI: birini diğerinin yerine göstermek,
           var olan yedekleri yok gibi sunmak olurdu. Aramanın boşa düşmesi DÖRDÜNCÜ bir durumdur:
           "hiç yedek yok" demek, süzgeci fark etmeyen kullanıcıya yedekleri yok gibi gösterirdi. -->
      <p v-if="loading && backups.length === 0" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <SectionLoadError
        v-else-if="listLoad.message.value && !listErrorInNotice"
        :message="listLoad.message.value"
        :busy="loading"
        @retry="refresh" />
      <p v-else-if="!listLoad.message.value && backups.length === 0" class="empty-state">{{ t("panel.backup.empty") }}</p>
      <p v-else-if="!listLoad.message.value && filteredBackups.length === 0" class="empty-state">{{ t("panel.backup.emptyFiltered") }}</p>
    </div>

    <TablePager v-if="activeView === 'list'" :state="pager" :disabled="busy" />

    <!-- ================= ARTIMSAL ZİNCİR ================= -->
    <section v-if="activeView === 'list' && chainRecord" class="form-surface chain-panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.chain.title") }}: {{ chainRecord.name }}</h3>
          <p class="meta">{{ t("panel.backup.chain.hint") }}</p>
        </div>
        <button class="secondary compact" @click="closeChain">{{ t("panel.common.cancel") }}</button>
      </div>

      <p v-if="chainLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <SectionLoadError
        v-else-if="chainLoad.message.value"
        :message="chainLoad.message.value"
        :busy="busy"
        @retry="reloadChain" />
      <template v-else-if="chain">
        <!-- KOPUK ZİNCİR: eksik halkanın ADI yazılır. "Bir şeyler eksik" demek, kullanıcının
             hangi yedeği geri getireceğini bilmemesi demektir. -->
        <p v-if="chain.broken" class="notice error" role="alert">
          <strong>{{ t("panel.backup.chain.broken") }}</strong><br />
          {{ t("panel.backup.chain.missing") }}: {{ chain.missingLinks.join(", ") || "—" }}
        </p>
        <p v-else class="field-hint">{{ t("panel.backup.chain.intact", { count: chain.links.length }) }}</p>

        <!-- ŞİFRELİ + ARTIMSAL: parola kaybı TEK HALKAYI değil ZİNCİRİN TAMAMINI götürür. -->
        <p v-if="chainRecord.encrypted" class="notice encryption-loss" role="status">
          {{ t("panel.backup.encryptionChainWarning") }}
        </p>

        <ol v-if="chain.links.length" class="chain-links">
          <li
            v-for="link in chain.links"
            :key="link.backupId"
            :class="['chain-item', { missing: !link.available, current: link.backupId === chainRecord.id }]">
            <span :class="['status-pill', link.backupMode === 'incremental' ? 'chain-link' : 'chain-head']">
              {{ link.backupMode === "incremental"
                ? t("panel.backup.chain.linkBadge", { index: link.chainIndex })
                : t("panel.backup.mode.full") }}
            </span>
            <span class="chain-name">
              {{ link.name }}
              <small v-if="link.backupId === chainRecord.id">{{ t("panel.backup.chain.currentLink") }}</small>
            </span>
            <span class="cell-muted">{{ new Date(link.createdAtUtc).toLocaleString(getFormattingLocale()) }}</span>
            <span class="cell-muted">{{ formatBytes(link.size) }}</span>
            <span :class="['status-pill', link.available ? 'ok' : 'danger']">
              {{ link.available ? t("panel.backup.chain.fileOk") : t("panel.backup.chain.fileMissing") }}
            </span>
          </li>
        </ol>
        <p v-else class="empty-state">{{ t("panel.backup.chain.none") }}</p>

        <div class="row-actions">
          <button
            v-if="isAdmin"
            class="compact"
            v-bind="writeGate(busy || chain.broken, chain.broken ? t('panel.backup.chain.restoreBlocked') : undefined)"
            @click="restore(chainRecord, true)">
            {{ t("panel.backup.restore") }}
          </button>
          <button class="danger compact"
            v-bind="writeGate(busy || chainDeletionPlan.ambiguous, chainDeletionPlan.ambiguous ? t('backup.chain_ambiguous') : undefined)"
            @click="removeChain(chainRecord)">
            {{ t("panel.backup.chain.delete") }}<template v-if="!chainDeletionPlan.ambiguous"> ({{ chainDeletionPlan.targets.length }})</template>
          </button>
        </div>
        <p class="field-hint">{{ t(chainDeletionPlan.ambiguous ? "backup.chain_ambiguous" : "panel.backup.chain.deleteWarning") }}</p>
      </template>
    </section>

    <!-- Ayar OKUNAMADIYSA kart yok olmaz: kullanıcı "böyle bir özellik yok" sanmasın diye
         sebebi yazılır ve tekrar deneme düğmesi verilir. -->
    <section v-if="activeView === 'settings' && isAdmin && !autoForm && autoLoad.message.value" class="form-surface auto-backup-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.autoTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.autoLoadFailed") }}</p>
        </div>
      </div>
      <SectionLoadError :message="autoLoad.message.value" :busy="busy" @retry="loadAutoBackup" />
    </section>

    <!-- Otomatik zamanlanmış yedek (Plesk gibi): motor Otomasyon servisidir; bu kart en çok aranan
         ayarları (sıklık + saklama) yedek ekranının içine taşır. Gelişmiş seçenekler Araçlar > Otomasyon'da. -->
    <section v-if="activeView === 'settings' && isAdmin && autoForm" class="form-surface auto-backup-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.autoTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.autoHint") }}</p>
        </div>
        <span :class="['status-pill', autoForm.autoBackupEnabled ? 'ok' : '']">
          {{ autoForm.autoBackupEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
      </div>
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoEnabled") }}</span>
          <select v-model="autoForm.autoBackupEnabled">
            <option :value="true">{{ t("panel.status.enabled") }}</option>
            <option :value="false">{{ t("panel.status.disabled") }}</option>
          </select>
        </label>
        <BackupScheduleFields :plan="autoSchedule" />
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoRetention") }}</span>
          <input v-model.number="autoForm.backupRetentionDays" type="number" min="0" />
        </label>
      </div>
      <!-- Saatin hangi saat dilimine ait olduğu SÖYLENİR; sunucu bunu bildirmediyse satır çizilmez. -->
      <p v-if="autoSchedule.mode !== 'interval' && autoServerZone" class="field-hint">
        {{ t("panel.schedule.serverTimeHint", { zone: autoServerZone, now: autoServerLocalTime }) }}
      </p>
      <!--
        SON ÇALIŞMANIN SONUCU. Önceden yalnız son/sonraki ZAMAN yazıyordu: kullanıcı "çalıştı"
        ile "çalıştı ama başarısız oldu"yu ayırt edemiyordu. `lastBackupStatus` ve
        `lastBackupDetails` zaten `GET /automation` yanıtında geliyor ve kısmi başarısızlığın
        sebebi ayrıntıda duruyor — göstermemek, sonucu yutmaktı.
      -->
      <p class="field-hint auto-run-line">
        {{ t("panel.backup.autoRuns", {
          last: autoStatus?.lastBackupRunUtc ? new Date(autoStatus.lastBackupRunUtc).toLocaleString(getFormattingLocale()) : "-",
          next: autoStatus?.nextBackupRunUtc ? new Date(autoStatus.nextBackupRunUtc).toLocaleString(getFormattingLocale()) : "-"
        }) }}
        <span v-if="autoStatus?.lastBackupStatus" :class="['status-pill', lastStatusTone(autoStatus.lastBackupStatus)]">
          {{ lastStatusText(autoStatus.lastBackupStatus) }}
        </span>
      </p>
      <p v-if="autoStatus?.lastBackupDetails" class="cell-muted last-details">{{ autoStatus.lastBackupDetails }}</p>
      <p class="field-hint">{{ t("panel.backup.autoAdvancedHint") }}</p>
      <div class="row-actions">
        <button class="compact" :disabled="busy" @click="saveAutoBackup">
          {{ t("panel.backup.autoSave") }}
        </button>
      </div>
    </section>

    <!--
      YEDEK ŞİFRELEME (yalnız yönetici; uç de `Administrator`).

      Parola alanları GERÇEK form alanı: `window.prompt` bu panelde yasak (parolayı düz metin
      gösterir ve tarayıcı diyalogları kapattığında sessizce null döner).
    -->
    <section v-if="activeView === 'settings' && isAdmin && encryption" class="form-surface encryption-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.encryptionTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.encryptionHint") }}</p>
        </div>
        <span :class="['status-pill', encryptionTone]">{{ t(encryptionStateKey) }}</span>
      </div>

      <!-- Kayıt var ama ÇÖZÜLEMİYOR: zamanlanmış yedekler şifrelenemez. Sessiz kalmak,
           yedeklerin sessizce şifresiz alınması demekti. -->
      <p v-if="encryption.enabled && !encryption.passwordUsable" class="notice error" role="alert">
        {{ t("panel.backup.encryptionBroken") }}
      </p>

      <!--
        ZORUNLU UYARI: parolayı BELİRLERKEN görünür, geri yükleme anında değil.

        İKİ UYARI TEK KUTUDA — metin BİREBİR aynı, kutu sayısı iki yerine bir.
        ÖLÇÜLDÜ: iki uyarı 189 + 116 karakter ve üst üste iki AYRI kenarlıklı kutu olarak
        çiziliyordu; iki kutu "iki ayrı konu" diye okunur, oysa ikisi de TEK bir gerçeği anlatır
        (parola kaybolursa veri geri gelmez). Ayrı kutular ayrıca kartın en üstünü 4 kenarlıkla
        (hata + kayıp + zincir + eşleşmeme) dolduruyordu.
        SİLİNEN METİN YOK: iki cümle de madde olarak tam hâliyle duruyor.
      -->
      <div class="notice encryption-loss" role="status">
        <ul class="warning-lines">
          <li>{{ t("panel.backup.encryptionLossWarning") }}</li>
          <li>{{ t("panel.backup.encryptionChainWarning") }}</li>
        </ul>
      </div>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPassword") }}</span>
          <PasswordField v-model="encryptionForm.password" autocomplete="new-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPasswordConfirm") }}</span>
          <PasswordField v-model="encryptionForm.confirm" autocomplete="new-password" />
        </label>
      </div>

      <label class="check-field">
        <input v-model="encryptionForm.acknowledged" type="checkbox" />
        <span>{{ t("panel.backup.encryptionAck") }}</span>
      </label>

      <p v-if="encryptionMismatch" class="notice error" role="alert">{{ t("panel.backup.encryptionMismatch") }}</p>

      <p class="field-hint">
        {{ t("panel.backup.encryptionAlgorithm") }}: {{ encryption.algorithm }}
        <template v-if="encryption.enabled">
          · {{ t("panel.backup.encryptionUpdatedAt") }}: {{ new Date(encryption.updatedAtUtc).toLocaleString(getFormattingLocale()) }}
        </template>
      </p>

      <div class="row-actions">
        <button class="compact" :disabled="busy || !canSaveEncryption" @click="saveEncryption">
          {{ t("panel.backup.encryptionSave") }}
        </button>
        <button v-if="encryption.enabled" class="danger compact" :disabled="busy" @click="disableEncryption">
          {{ t("panel.backup.encryptionDisable") }}
        </button>
      </div>
    </section>

    <!-- ================= YEDEK İÇERİĞİ + SEÇEREK GERİ YÜKLEME ================= -->
    <section v-if="activeView === 'list' && browseRecord" class="form-surface backup-browser">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.browseTitle") }}</h3>
          <p class="archive-name">{{ browseRecord.name }}</p>
          <p class="meta">{{ t("panel.backup.browseHint") }}</p>
        </div>
        <button type="button" class="secondary compact" @click="closeBrowser">{{ t("panel.backup.verify.close") }}</button>
      </div>

      <nav class="breadcrumb" :aria-label="t('panel.backup.browsePath')">
        <button
          class="secondary compact"
          :disabled="busy"
          :aria-label="t('panel.backup.browseRoot')"
          :title="t('panel.backup.browseRoot')"
          :aria-current="!browsePrefix ? 'location' : undefined"
          @click="navigateTo('')">{{ t('panel.backup.browseRoot') }}</button>
        <button
          v-for="crumb in breadcrumbs"
          :key="crumb.prefix"
          class="secondary compact"
          :disabled="busy"
          :aria-current="crumb.prefix === browsePrefix ? 'location' : undefined"
          @click="navigateTo(crumb.prefix)">
          {{ crumb.name }}
        </button>
      </nav>

      <div class="data-table archive-entries" :aria-busy="browseLoading">
        <div class="data-row table-head entry-row">
          <span></span>
          <span>{{ t("panel.backup.name") }}</span>
          <span>{{ t("panel.backup.size") }}</span>
        </div>
        <!-- Kökte girdiler ÖNEKLERİNE göre gruplanır (Dosyalar / Veritabanları / Posta / FTP):
             abonelik arşivinin kökü aksi hâlde anlamsız dört klasör adı olurdu. -->
        <template v-for="group in groupedEntries" :key="group.key || 'flat'">
          <div v-if="group.key" class="entry-group-head">
            <strong>{{ t(`panel.backup.group.${group.key}`) }}</strong>
            <span class="cell-muted">{{ group.entries.length }}</span>
          </div>
          <div v-for="entry in group.entries" :key="entry.path" class="data-row entry-row">
            <input v-model="selectedItems" type="checkbox" :value="entry.path" :aria-label="entry.name" :disabled="busy || restoreItemsBusy || downloadItemsBusy" />
            <button
              v-if="entry.isDirectory"
              class="secondary compact entry-link"
              :disabled="busy"
              @click="navigateTo(entry.path)">
              <span aria-hidden="true">📁</span><span>{{ entry.name }}</span>
            </button>
            <strong v-else class="entry-file">{{ entry.name }}</strong>
            <span class="cell-muted">{{ entry.isDirectory ? "—" : formatBytes(entry.size) }}</span>
          </div>
        </template>
        <p v-if="browseLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
        <SectionLoadError
          v-else-if="browseLoad.message.value"
          :message="browseLoad.message.value"
          :busy="busy"
          @retry="navigateTo(browsePrefix)" />
        <p v-else-if="browseEntries.length === 0" class="empty-state">{{ t("panel.backup.browseEmpty") }}</p>
      </div>
      <p v-if="browseTruncated" class="field-hint">{{ t("panel.backup.browseTruncated") }}</p>

      <section class="archive-selection" :aria-label="t('panel.backup.selectedItemsTitle')">
        <div class="section-head">
          <h4>{{ t('panel.backup.selectedItemsTitle') }} <span class="status-pill">{{ selectedItems.length }}</span></h4>
          <button v-if="selectedItems.length" type="button" class="secondary compact" :disabled="busy || restoreItemsBusy || downloadItemsBusy" @click="selectedItems = []">
            {{ t("panel.bulk.clearSelection") }}
          </button>
        </div>
        <p class="field-hint">{{ t(selectedItems.length ? 'panel.backup.selectionKept' : 'panel.backup.selectionEmpty') }}</p>
        <ul v-if="selectedItems.length" class="selected-paths">
          <li v-for="path in selectedItems" :key="path">
            <code>{{ path }}</code>
            <button type="button" class="secondary compact" :disabled="busy || restoreItemsBusy || downloadItemsBusy" :aria-label="t('panel.backup.removeSelection', { path })" @click="selectedItems = selectedItems.filter(item => item !== path)">×</button>
          </li>
        </ul>
      </section>

      <p class="field-hint">{{ t('panel.backup.selectedDownloadHint') }}</p>
      <div class="row-actions">
        <button type="button" class="compact" :disabled="!canDownloadSelectedItems" @click="downloadSelectedItems">
          {{ t(downloadItemsBusy ? 'panel.backup.downloading' : 'panel.backup.downloadSelected') }} ({{ selectedItems.length }})
        </button>
      </div>
      <details v-if="canChooseItemsTarget" class="archive-destination">
        <summary>{{ t('panel.backup.extractOnServer') }}</summary>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.itemsTarget") }}</span>
          <input v-model.trim="itemsTargetPath" :disabled="busy || restoreItemsBusy || downloadItemsBusy" :placeholder="t('panel.backup.itemsTargetOriginal')" />
        </label>
        <p class="field-hint">{{ t(itemsTargetPath ? 'panel.backup.customExtractHint' : 'panel.backup.separateExtractHint') }}</p>
        <div class="row-actions">
          <button
            class="secondary compact"
            v-bind="writeGate(!canRestoreItems)"
            @click="restoreSelectedItems">
            {{ t("panel.backup.restoreSelected") }} ({{ selectedItems.length }})
          </button>
        </div>
      </details>
    </section>

    <!-- ================= YENİ YEDEK ================= -->
    <section v-if="showCreateForm" class="form-surface backup-create">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.new") }}</h3>
          <p class="meta">{{ backupKindHint }}</p>
        </div>
        <div class="segmented-tabs" role="tablist">
          <button role="tab" :aria-selected="backupKind === 'site'" :class="{ active: backupKind === 'site' }" @click="backupKind = 'site'">
            {{ t("panel.backup.siteTitle") }}
          </button>
          <button role="tab" :aria-selected="backupKind === 'subscription'" :class="{ active: backupKind === 'subscription' }" @click="backupKind = 'subscription'">
            {{ t("panel.backup.subscription.title") }}
          </button>
          <button v-if="isAdmin" role="tab" :aria-selected="backupKind === 'database'" :class="{ active: backupKind === 'database' }" @click="backupKind = 'database'">
            {{ t("panel.backup.dbTitle") }}
          </button>
          <button v-if="isAdmin" role="tab" :aria-selected="backupKind === 'mail'" :class="{ active: backupKind === 'mail' }" @click="backupKind = 'mail'">
            {{ t("panel.backup.mailTitle") }}
          </button>
          <button role="tab" :aria-selected="backupKind === 'panel'" v-if="isAdmin" :class="{ active: backupKind === 'panel' }" @click="backupKind = 'panel'">
            {{ t("panel.backup.panelTitle") }}
          </button>
        </div>
      </div>

      <div class="form-grid">
        <label v-if="backupKind === 'site'" class="stacked-field">
          <span class="field-label">{{ t("panel.php.targetSite") }}</span>
          <select v-model="siteName" required>
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
          </select>
        </label>
        <label v-else-if="backupKind === 'subscription'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.subscription.customer") }}</span>
          <select v-model="subscriptionUserId">
            <option value="">{{ t("panel.backup.subscription.selectCustomer") }}</option>
            <option v-for="choice in subscriptionChoices" :key="choice.id" :value="choice.id">
              {{ choice.label }}
            </option>
          </select>
        </label>
        <template v-else-if="backupKind === 'database'">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.engineLabel") }}</span>
            <select v-model="databaseEngine">
              <option value="mariadb">MariaDB</option>
              <option value="mysql">MySQL</option>
              <option value="postgresql">PostgreSQL</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.databaseName") }}</span>
            <input v-model="databaseName" :placeholder="t('panel.backup.allDatabases')" />
          </label>
        </template>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.targetType") }}</span>
          <select v-model="targetForm.targetType">
            <option value="local">{{ t("panel.backup.target.local") }}</option>
            <option value="ftp">{{ t("panel.backup.target.ftp") }}</option>
            <option value="s3">{{ t("panel.backup.target.s3") }}</option>
            <option value="webdav">{{ t("panel.backup.target.webdav") }}</option>
            <option value="dropbox">{{ t("panel.backup.target.dropbox") }}</option>
            <option value="onedrive">{{ t("panel.backup.target.onedrive") }}</option>
            <option value="azure">{{ t("panel.backup.target.azure") }}</option>
          </select>
        </label>
      </div>

      <!-- Abonelik kapsamı: hepsi kapatılırsa arşivde manifestten başka bir şey kalmaz. -->
      <template v-if="backupKind === 'subscription'">
        <p class="field-hint">{{ t("panel.backup.subscription.hint") }}</p>
        <p v-if="subscriptionChoices.length === 0" class="notice warn" role="status">
          {{ t("panel.backup.subscription.noCustomers") }}
        </p>
        <div class="scope-grid">
          <label class="check-field">
            <input v-model="subscriptionScope.includeFiles" type="checkbox" />
            <span>{{ t("panel.backup.scopeFiles") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeDatabases" type="checkbox" />
            <span>{{ t("panel.backup.scopeDatabases") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeMail" type="checkbox" />
            <span>{{ t("panel.backup.scopeMail") }}</span>
          </label>
          <label class="check-field">
            <input v-model="subscriptionScope.includeFtp" type="checkbox" />
            <span>{{ t("panel.backup.scopeFtp") }}</span>
          </label>
        </div>
        <!-- ZORUNLU NOT: posta bölümü ENVANTERDİR, iletiler değil. -->
        <p class="notice" role="status">{{ t("panel.backup.subscription.mailNote") }}</p>
      </template>

      <!-- Artımsal yedek: yalnız DOSYA taşıyan yedeklerde anlamlıdır (site + abonelik). -->
      <div v-if="backupKind === 'site' || backupKind === 'subscription'" class="form-grid">
        <label class="check-field">
          <input v-model="incrementalForm.incremental" type="checkbox" />
          <span>{{ t("panel.backup.incremental") }}</span>
        </label>
        <label v-if="incrementalForm.incremental" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.schedule.fullEvery") }}</span>
          <input v-model.number="incrementalForm.fullBackupEveryDays" type="number" min="1" max="365" />
        </label>
      </div>
      <p v-if="(backupKind === 'site' || backupKind === 'subscription') && incrementalForm.incremental" class="field-hint">
        {{ t("panel.backup.incrementalHint") }}
      </p>

      <div v-if="targetForm.targetType !== 'local'" class="form-grid">
        <label class="stacked-field remote-url">
          <!-- Dropbox/OneDrive'da "URL" diye bir kavram YOKTUR; kullanıcı yalnızca hesabındaki
               KLASÖR YOLUNU yazar. Etiketi de ona göre değişir, yoksa kullanıcı uydurma bir
               adres arayıp bulamaz. -->
          <span class="field-label">{{ isFolderPathTarget(targetForm.targetType) ? t("panel.backup.remoteFolder") : t("panel.backup.remoteUrl") }}</span>
          <input v-model="targetForm.remoteUrl" :placeholder="remoteUrlPlaceholder" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ remoteUserLabel(targetForm.targetType) }}</span>
          <input v-model="targetForm.remoteUserName" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ remoteSecretLabel(targetForm.targetType) }}</span>
          <PasswordField v-model="targetForm.remotePassword" autocomplete="new-password" />
        </label>
        <label v-if="targetForm.targetType === 's3'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.remoteRegion") }}</span>
          <input v-model="targetForm.remoteRegion" placeholder="eu-central-1" />
        </label>
        <label v-if="targetForm.targetType === 'onedrive'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.remoteTenant") }}</span>
          <input v-model="targetForm.remoteRegion" placeholder="common" />
        </label>
      </div>
      <!-- Yeni bulut hedeflerinde kimliğin NE olduğu kutunun yanında yazılı olmalı: jeton mu,
           yenileme jetonu mu, hesap anahtarı mı — yanlış değeri yapıştırmak gece yedeğini
           sessizce başarısız kılar. -->
      <p v-if="cloudTargetHint" class="field-hint">{{ cloudTargetHint }}</p>

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.perBackupPassword") }}</span>
          <PasswordField v-model="targetForm.encryptionPassword" autocomplete="new-password" />
          <small class="field-hint">{{ t("panel.backup.perBackupPasswordHint") }}</small>
        </label>
      </div>

      <!-- Şifreli ZİNCİRDE parola kaybı TEK halkayı değil zincirin TAMAMINI götürür; uyarı,
           parolanın yazıldığı yerde durur. -->
      <p
        v-if="incrementalForm.incremental && (backupKind === 'site' || backupKind === 'subscription')"
        class="notice encryption-loss"
        role="status">
        {{ t("panel.backup.encryptionChainWarning") }}
      </p>

      <!-- Uzak hedefe ŞİFRESİZ gitmek üzere: uyarı gönderimden ÖNCE, kullanıcı parolayı hâlâ
           yazabilecekken görünür (sonradan gelen `backup.remote_unencrypted` rozeti geç kalır). -->
      <p v-if="remoteUnencryptedRisk" class="notice encryption-loss" role="status">
        {{ t("panel.backup.remoteUnencryptedWarning") }}
      </p>

      <p class="field-hint">{{ t("panel.backup.targetHint") }}</p>
      <div class="row-actions">
        <!-- Düğme metni İŞ SÜRERKEN değişir: `disabled` tek başına "sürüyor" demiyordu ve
             kullanıcı ekranın donduğunu sanıp sayfayı yeniliyordu (ikinci bir yedek tetikler). -->
        <button class="compact" v-bind="writeGate(busy)" @click="createSelectedBackup">
          {{ busy ? t("panel.backup.creating") : createButtonLabel }}
        </button>
        <button v-if="isAdmin && backupKind === 'database'" class="secondary compact" v-bind="writeGate(busy)" @click="createDatabaseToConfiguredFtp">
          {{ t("panel.backup.createToConfiguredFtp") }}
        </button>
        <button class="secondary compact" @click="activeView = 'list'">{{ t("panel.common.cancel") }}</button>
      </div>
    </section>

    <!-- ================= ABONELİK YEDEK PROGRAMLARI ================= -->
    <section v-if="activeView === 'schedules'" class="form-surface schedules-panel">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.schedule.title") }}</h3>
          <p class="meta">{{ t("panel.backup.schedule.hint") }}</p>
        </div>
        <button class="secondary compact" :disabled="schedulesLoading" @click="loadSchedules">
          {{ t("panel.backup.refresh") }}
        </button>
      </div>

      <div class="data-table">
        <div class="data-row table-head schedule-row">
          <span>{{ t("panel.backup.schedule.customer") }}</span>
          <span>{{ t("panel.backup.statusColumn") }}</span>
          <span>{{ t("panel.schedule.mode") }}</span>
          <span>{{ t("panel.backup.schedule.retentionColumn") }}</span>
          <span>{{ t("panel.backup.schedule.mode") }}</span>
          <span>{{ t("panel.backup.schedule.lastRun") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="schedule in schedulePager.items.value" :key="schedule.userId" class="data-row schedule-row">
          <div class="cell-main">
            <strong>{{ schedule.userName || schedule.userId }}</strong>
            <small>{{ schedule.planName || t("panel.backup.schedule.noPlan") }}</small>
          </div>
          <div class="cell-main">
            <span :class="['status-pill', schedule.enabled ? 'ok' : '']">
              {{ schedule.enabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
            <small v-if="schedule.throttledByQuota" class="quota-warn">
              {{ t("panel.backup.schedule.throttled", { hours: schedule.intervalHours }) }}
            </small>
          </div>
          <div class="cell-main">
            <!-- Özet metni TEK KAYNAKTAN (api/schedule.ts): "her gün 03:00" ile form alanları
                 birbirinden sapamasın. -->
            <strong>{{ programOzeti(scheduleToPlan(schedule), t) }}</strong>
            <small>{{ t("panel.backup.schedule.nextRun") }}: {{ formatDate(schedule.nextRunUtc) }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ retentionLabel(schedule) }}</strong>
            <small>{{ targetLabel(schedule.targetType) }}</small>
          </div>
          <div class="cell-main">
            <strong>{{ schedule.incremental ? t("panel.backup.mode.incremental") : t("panel.backup.mode.full") }}</strong>
            <small v-if="schedule.incremental">
              {{ t("panel.backup.schedule.fullEvery") }}: {{ schedule.fullBackupEveryDays }}
            </small>
          </div>
          <div class="cell-main">
            <span :class="['status-pill', lastStatusTone(schedule.lastStatus)]">{{ lastStatusLabel(schedule) }}</span>
            <small>{{ formatDate(schedule.lastRunUtc) }}</small>
            <!-- Kısmi başarısızlık SEBEBİYLE birlikte durur; "çalıştı" demek yetmez. -->
            <small v-if="schedule.lastDetails" class="last-details">{{ schedule.lastDetails }}</small>
          </div>
          <div class="row-actions">
            <button class="secondary compact row-button" :disabled="busy" @click="openScheduleForm(schedule.userId)">
              {{ t("panel.common.edit") }}
            </button>
            <button class="secondary compact row-button" v-bind="writeGate(busy)" @click="runScheduleNow(schedule)">
              {{ t("panel.backup.schedule.runNow") }}
            </button>
            <button class="danger compact row-button" v-bind="writeGate(busy)" @click="removeSchedule(schedule)">
              {{ t("panel.backup.schedule.remove") }}
            </button>
          </div>
        </div>
        <p v-if="schedulesLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
        <SectionLoadError
          v-else-if="schedulesLoad.message.value && !schedulesErrorInNotice"
          :message="schedulesLoad.message.value"
          :busy="schedulesLoading"
          @retry="loadSchedules" />
        <p v-else-if="!schedulesLoad.message.value && schedules.length === 0" class="empty-state">{{ t("panel.backup.schedule.empty") }}</p>
      </div>
      <TablePager :state="schedulePager" :disabled="busy" />

      <div class="form-grid two schedule-add">
        <label v-if="canPickSubscription" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.subscription.customer") }}</span>
          <select v-model="newScheduleUserId">
            <option value="">{{ t("panel.backup.subscription.selectCustomer") }}</option>
            <option v-for="choice in subscriptionChoices" :key="choice.id" :value="choice.id">
              {{ choice.label }}
            </option>
          </select>
        </label>
        <div class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(busy || !newScheduleUserId, newScheduleUserId ? undefined : t('panel.backup.subscription.required'))"
            @click="openScheduleForm(newScheduleUserId)">
            {{ t("panel.backup.schedule.add") }}
          </button>
        </div>
      </div>
      <p v-if="!canPickSubscription && !ownSubscriptionId" class="notice warn" role="status">
        {{ t("panel.backup.schedule.noOwnSubscription") }}
      </p>

      <form v-if="scheduleForm" class="form-surface schedule-form" @submit.prevent="saveSchedule">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.backup.schedule.editTitle", { name: scheduleForm.userName || scheduleForm.userId }) }}</h3>
            <p class="meta">{{ scheduleForm.planName || t("panel.backup.schedule.noPlan") }}</p>
          </div>
          <button type="button" class="secondary compact" @click="closeScheduleForm">
            {{ t("panel.common.cancel") }}
          </button>
        </div>

        <!-- Kota kullanıcının GİRDİĞİ sıklığı kabul etmemiş olabilir: sunucu KOTAYA ÇEKİLMİŞ
             değeri döner ve form onunla tazelenir. -->
        <p v-if="scheduleThrottled" class="notice warn" role="alert">
          {{ t("panel.backup.schedule.throttled", { hours: scheduleForm.plan.intervalHours }) }}
        </p>

        <div class="form-grid">
          <label class="check-field">
            <input v-model="scheduleForm.enabled" type="checkbox" />
            <span>{{ t("panel.backup.schedule.enabled") }}</span>
          </label>
          <!-- Abonelik programı da SAAT seçebilir: sunucu geneli programla aynı alanlar, aynı
               bileşen. Kota YALNIZCA aralık modunda daraltır (takvim modu zaten günde en çok
               bir tur üretir). -->
          <BackupScheduleFields :plan="scheduleForm.plan" />
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.retentionDays") }}</span>
            <input v-model.number="scheduleForm.retentionDays" type="number" min="0" max="3650" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.keepCount") }}</span>
            <input v-model.number="scheduleForm.keepCount" type="number" min="0" max="1000" />
          </label>
        </div>

        <div class="form-grid">
          <label class="check-field">
            <input v-model="scheduleForm.incremental" type="checkbox" />
            <span>{{ t("panel.backup.schedule.incremental") }}</span>
          </label>
          <label v-if="scheduleForm.incremental" class="stacked-field">
            <span class="field-label">{{ t("panel.backup.schedule.fullEvery") }}</span>
            <input v-model.number="scheduleForm.fullBackupEveryDays" type="number" min="1" max="365" />
          </label>
        </div>
        <p v-if="scheduleForm.incremental" class="notice encryption-loss" role="status">
          {{ t("panel.backup.encryptionChainWarning") }}
        </p>

        <span class="field-label">{{ t("panel.backup.schedule.scope") }}</span>
        <div class="scope-grid">
          <label class="check-field">
            <input v-model="scheduleForm.includeFiles" type="checkbox" />
            <span>{{ t("panel.backup.scopeFiles") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeDatabases" type="checkbox" />
            <span>{{ t("panel.backup.scopeDatabases") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeMail" type="checkbox" />
            <span>{{ t("panel.backup.scopeMail") }}</span>
          </label>
          <label class="check-field">
            <input v-model="scheduleForm.includeFtp" type="checkbox" />
            <span>{{ t("panel.backup.scopeFtp") }}</span>
          </label>
        </div>
        <p class="field-hint">{{ t("panel.backup.subscription.mailNote") }}</p>

        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.backup.targetType") }}</span>
            <select v-model="scheduleForm.targetType">
              <option value="local">{{ t("panel.backup.target.local") }}</option>
              <option value="ftp">{{ t("panel.backup.target.ftp") }}</option>
              <option value="s3">{{ t("panel.backup.target.s3") }}</option>
              <option value="webdav">{{ t("panel.backup.target.webdav") }}</option>
            </select>
          </label>
          <template v-if="scheduleForm.targetType !== 'local'">
            <label class="stacked-field remote-url">
              <span class="field-label">{{ t("panel.backup.remoteUrl") }}</span>
              <input v-model="scheduleForm.remoteUrl" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ scheduleForm.targetType === 's3' ? t("panel.backup.remoteAccessKey") : t("panel.backup.remoteUser") }}</span>
              <input v-model="scheduleForm.remoteUserName" autocomplete="off" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ scheduleForm.targetType === 's3' ? t("panel.backup.remoteSecretKey") : t("panel.backup.remotePassword") }}</span>
              <PasswordField v-model="scheduleForm.remotePassword" autocomplete="new-password" />
              <small class="field-hint">
                {{ scheduleForm.remotePasswordSet
                  ? t("panel.backup.schedule.passwordKept")
                  : t("panel.backup.schedule.passwordEmpty") }}
              </small>
            </label>
            <label v-if="scheduleForm.targetType === 's3'" class="stacked-field">
              <span class="field-label">{{ t("panel.backup.remoteRegion") }}</span>
              <input v-model="scheduleForm.remoteRegion" placeholder="eu-central-1" />
            </label>
          </template>
        </div>

        <!-- Plan kotası KALICI olarak görünür: kullanıcı sıklığı yazarken sınırı bilmelidir. -->
        <p v-if="scheduleForm.maxBackupsPerDay > 0" class="field-hint">
          {{ t("panel.backup.schedule.quotaHint", { count: scheduleForm.maxBackupsPerDay }) }}
        </p>

        <div class="row-actions">
          <button type="submit" class="compact" v-bind="writeGate(busy)">{{ t("panel.common.save") }}</button>
        </div>
      </form>
    </section>
  </section>
</template>

<script setup lang="ts">
import { getFormattingLocale } from '../utils/locale';
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import BackupRestoreResult from "../components/BackupRestoreResult.vue";
import BackupScheduleFields from "../components/BackupScheduleFields.vue";
import { bosProgram, programOzeti, type BackupSchedulePlan } from "../api/schedule";
import {
  backupDatabase,
  backupMail,
  backupSite,
  backupPanel,
  backupSubscription,
  deleteBackup,
  retryRemoteUpload,
  deleteBackupChain,
  deleteBackupSchedule,
  downloadBackup,
  downloadBackupItems,
  getBackupChain,
  getBackupEncryption,
  getBackupEntries,
  getBackupRunningJobs,
  getBackups,
  getBackupSchedule,
  getBackupSchedules,
  restoreBackupItems,
  runBackupScheduleNow,
  setBackupEncryption,
  updateBackupSchedule,
  verifyBackups,
  type BackupChain,
  type BackupEncryptionSettings,
  type BackupEntry,
  type BackupRecord,
  type BackupRestoreWarning,
  type BackupTargetPayload,
  type BackupVerificationResult,
  type SubscriptionBackupSchedule
} from "../api/backup";
import { getAutomation, updateAutomation, type AutomationSettings, type AutomationStatus } from "../api/automation";
import { getPanelPreferences } from "../api/panelSettings";
import { getMySubscription } from "../api/subscriptions";
import { useBackupRestore, restoreResult } from "../composables/useBackupRestore";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import TablePager from "../components/TablePager.vue";
import { getSessionEpoch } from "../api/client";
import { compareNewestFirst, localDayKey, useDayGroups } from "../composables/useDayGroups";
import { useNotice, useSectionError } from "../composables/useNotice";
import { usePagination } from "../composables/usePagination";
import { runningJobLabel, useRunningTask, useServerRunningTask } from "../composables/useRunningTask";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { copyToClipboard } from "../utils/clipboard";
import { maskSecretsInUri } from "../utils/url";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";
import { getBackupDeletionPlan } from "../utils/backupDeletionPlan";

const { t } = useI18n();
const { confirm } = useConfirm();
const fullRestore = useBackupRestore();
const { prompt } = usePrompt();
const { writeGate, locked } = useSubscriptionGate();
const { notice, noticeError, showNotice, showError, resolveApiMessage } = useNotice();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");
const backups = ref<BackupRecord[]>([]);
const listConfirmed = ref(false);
const activeView = ref<"list" | "create" | "schedules" | "settings">("list");
const backupKind = ref<"site" | "subscription" | "database" | "mail" | "panel">("site");
const expandedWarnings = ref("");
/** Yolu açık olan kayıt (tek seferde bir tane): tam yollar uzun, hepsi birden açık kalmasın. */
const expandedLocation = ref("");
const search = ref("");
const viewEpoch = getSessionEpoch();
const viewSession = authStore.session, viewActorId = viewSession?.user?.id, viewActorRole = viewSession?.user?.role;
let disposed = false;
let listRequest = 0;
let chainRequest = 0;
const isActive = () => !disposed && viewEpoch === getSessionEpoch() && authStore.session === viewSession &&
  authStore.session?.isAuthenticated === true && authStore.session.user?.id === viewActorId && authStore.session.user?.role === viewActorRole;
const canWrite = () => isActive() && !locked.value;
const canAdminWrite = () => canWrite() && isAdmin.value;
onBeforeUnmount(() => { disposed = true; });

/** Yedek listesi ÇEKİLEMEDİ. Boş listeyle karıştırılmaz: biri "yok", diğeri "bilmiyoruz". */
const listLoad = useSectionError();
const listErrorInNotice = computed(() => !!listLoad.message.value && noticeError.value && notice.value.includes(listLoad.message.value));

// Süren yedek işi GÖRÜNÜR (bkz. composables/useRunningTask.ts): uç işi arka plana almadığı için
// istek uçuştayken yedek gerçekten sürüyor.
const runningBackup = useRunningTask();

/*
 * SAYFA YENİLENDİĞİNDE "YEDEK/GERİ YÜKLEME SÜRÜYOR" ARTIK KAYBOLMUYOR.
 *
 * ÖNCESİ: durum yalnız bu sekmenin belleğindeydi. Abonelik yedeği (bir müşterinin bütün siteleri,
 * veritabanları ve postası tek arşivde) saatler sürebiliyor; kullanıcı sayfayı yenilediğinde panel
 * "iş yok" diyor, yedek sunucuda sürerken ekran boş görünüyordu.
 *
 * ŞİMDİ: `/backup/running` süren işi (hedef + SUNUCUDAKİ başlangıç anı) döndürüyor. Panel iş
 * sürerken yeniden başlamışsa aynı uç bunu "yarıda kaldı" olarak yüzeye çıkarır — sessizce boş
 * bir ekran, "yedeğim var sanıyordum" felaketinin ilk adımıdır.
 */
const serverBackup = useServerRunningTask({
  fetchStatus: getBackupRunningJobs,
  label: (job) => runningJobLabel(t, job),
  interruptedLabel: (job) =>
    t("panel.runningJob.interrupted", { job: runningJobLabel(t, job), date: formatDate(job.startedAtUtc) })
});

// Bu sekmede başlatılan iş varsa ONUN sayacı gösterilir (saniyelik ve kesin); yoksa sunucudan
// okunan iş.
const bannerRunning = computed(() => runningBackup.running.value || serverBackup.running.value);
const bannerLabel = computed(() =>
  runningBackup.running.value ? runningBackup.label.value : serverBackup.label.value);
const bannerElapsedText = computed(() =>
  runningBackup.running.value ? runningBackup.elapsedText.value : serverBackup.elapsedText.value);
// Sunucudan okunan iş bu sekmede başlatılmadı: "sayfadan ayrılmayın" demek yanlış olurdu.
const bannerHint = computed(() =>
  runningBackup.running.value ? "" : t("panel.runningJob.resumedHint"));
const { groupByDay } = useDayGroups();

/**
 * Kapsamlı kullanıcının KENDİ aboneliği (alt-kullanıcıda ana aboneliğin kimliği).
 *
 * Burada duruyor çünkü `backupSourceLabel` bunu okuyor ve o da ARAMA süzgecinden çağrılıyor:
 * `const` bildirimleri hoisted DEĞİLDİR, aşağıda kalsaydı süzgecin ilk değerlendirmesi
 * "Cannot access before initialization" ile paneli çökertebilirdi.
 */
const ownSubscriptionId = ref("");
const ownSubscriptionName = ref("");

/**
 * SÜZÜLMÜŞ ve zamana göre SIRALI liste — gruplamanın ve sayfalamanın tek kaynağı.
 *
 * Arama TÜM kayıtlarda çalışır (kapalı gün ya da başka sayfa fark etmez) ve sayfalamadan ÖNCE
 * uygulanır; aksi hâlde kullanıcı yalnız açık sayfanın içinde aradığını sanır (bkz.
 * composables/usePagination.ts).
 *
 * SIRALAMA burada da yapılır: uç şu an `createdAtUtc` azalan döndürüyor, ama gün gruplamasının
 * DOĞRULUĞU (bir günün listede tek bir grup olması) sunucunun sıralamasına bağlı bırakılamaz.
 */
const filteredBackups = computed(() => {
  const query = search.value.trim().toLowerCase();
  const rows = query
    ? backups.value.filter((backup) => [
        backup.name,
        backup.source,
        backupSourceLabel(backup),
        backupKindLabel(backup.type),
        backup.status,
        backup.path,
        backup.id
      ].some((field) => (field ?? "").toLowerCase().includes(query)))
    : [...backups.value];

  return rows.sort((left, right) => compareNewestFirst(left.createdAtUtc, right.createdAtUtc));
});

/**
 * SAYFALAMA KAYITLARA uygulanır, GÜNLERE değil — bilinçli.
 *
 * Günleri sayfalamak DOM'u sınırlamaz: tek bir günde yüzlerce yedek olabilir (abonelik başına
 * program + saatlik sıklık) ve o gün açıkken tarayıcı yine kilitlenirdi; `usePagination`ın var
 * olma sebebi tam olarak budur. Kayıt sayfalaması ise üst sınırı KULLANICI NE YAPARSA YAPSIN
 * korur. Liste zamana göre sıralı olduğundan bir sayfa her zaman kesintisiz bir zaman aralığıdır
 * ve bir gün en fazla bir sayfa sınırında bölünür; bölündüğünde başlıkta "bu sayfada N / M" yazar.
 *
 * Gün grubunu KAPATMAK yalnızca çizimi etkiler, sayfalamayı DEĞİŞTİRMEZ: kapatmanın sayfa
 * içeriğini değiştirmesi, kullanıcının bir günü kapatınca başka bir günün satırlarının
 * kaymasına yol açardı.
 */
const pager = usePagination(filteredBackups);
const pageGroups = computed(() => groupByDay(pager.items.value, (backup) => backup.createdAtUtc));

/**
 * Gün başlığındaki özet, SÜZÜLMÜŞ LİSTENİN TAMAMINDAN ölçülür (açık sayfadan değil): bir gün
 * sayfa sınırında bölündüğünde başlıktaki "kaç yedek / toplam boyut" yine o GÜNÜN gerçeğidir.
 */
const dayTotals = computed(() => {
  const totals = new Map<string, { count: number; size: number }>();
  for (const backup of filteredBackups.value) {
    const key = localDayKey(backup.createdAtUtc);
    const current = totals.get(key) ?? { count: 0, size: 0 };
    current.count += 1;
    current.size += backup.size;
    totals.set(key, current);
  }
  return totals;
});

function dayStats(key: string) {
  return dayTotals.value.get(key) ?? { count: 0, size: 0 };
}

/**
 * Kapalı gün anahtarları.
 *
 * "Kapalı olanları" tutuyoruz, "açık olanları" değil: en yeni gün AÇIK başlamalı ve yeni bir gün
 * geldiğinde de kendiliğinden açık olmalı. Açık kümesi tutulsaydı, yarın alınan ilk yedek kapalı
 * bir günün altına düşer ve kullanıcı onu göremezdi.
 */
const collapsedDays = ref(new Set<string>());

function isDayOpen(key: string) {
  return !collapsedDays.value.has(key);
}

function toggleDay(key: string) {
  // Set YERİNDE değiştirilirse `ref` tetiklenmez; yeni bir Set atanır.
  const next = new Set(collapsedDays.value);
  if (next.has(key)) {
    next.delete(key);
  } else {
    next.add(key);
  }
  collapsedDays.value = next;
}

/**
 * En yeni gün AÇIK, geri kalan günler KAPALI başlar.
 *
 * Yalnız İLK yüklemede uygulanır (`dayDefaultsApplied`): her yenilemede yeniden uygulanmak,
 * kullanıcının elle açtığı eski bir günü her yenilemede kapatmak olurdu.
 */
const dayDefaultsApplied = ref(false);

watch(filteredBackups, (rows) => {
  if (dayDefaultsApplied.value || rows.length === 0) {
    return;
  }

  const keys = [...new Set(rows.map((backup) => localDayKey(backup.createdAtUtc)))];
  // Liste zamana göre azalan sıralı: ilk anahtar EN YENİ gündür.
  collapsedDays.value = new Set(keys.slice(1));
  dayDefaultsApplied.value = true;
}, { immediate: true });

// Arama listeyi daralttığında açık sayfa aralık dışında kalabilir; `usePagination` sayfayı
// geçerli aralığa çekiyor ama kullanıcı 4. sayfada arama yaptığında BAŞA dönmesi gerekir.
watch(search, () => pager.reset());

// --- YEDEĞİN DİSKTEKİ YERİ ---

/**
 * Yedeklerin KÖK klasörü (gün klasörleri bunun altında). Kaynak: Ayarlar'daki
 * `defaultBackupRoot` tercihi — uydurulmaz. `GET /panel-settings` YALNIZ yöneticiye açık
 * olduğundan kiracıda boş kalır ve satır hiç çizilmez ("okunamadıysa gösterme").
 */
const backupRoot = ref("");

function toggleLocation(backupId: string) {
  expandedLocation.value = expandedLocation.value === backupId ? "" : backupId;
}

/** Uzak adresi kimliksiz + imzasız gösterime çevirir (bkz. utils/url.ts). */
function safeRemote(remoteUri: string) {
  return maskSecretsInUri(remoteUri);
}

async function copyPath(value: string) {
  if (!value || !isActive()) {
    return;
  }

  // Kopyalanmadıysa "kopyalandı" DEMEYİZ: `copyToClipboard` gerçekten kopyalayıp kopyalamadığını
  // döndürür (HTTP üzerinde pano izni verilmeyebilir).
  const copied = await copyToClipboard(value);
  if (!isActive()) return;
  showNotice(copied ? t("panel.common.copied") : t("panel.common.copyFailed"), !copied);
}

const backupKindHint = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteHint");
  if (backupKind.value === "subscription") return t("panel.backup.subscription.hint");
  if (backupKind.value === "database") return t("panel.backup.dbHint");
  if (backupKind.value === "panel") return t("panel.backup.panelHint");
  return t("panel.backup.mailHint");
});

const createButtonLabel = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteAction");
  if (backupKind.value === "subscription") return t("panel.backup.subscription.action");
  if (backupKind.value === "mail") return t("panel.backup.mailAction");
  if (backupKind.value === "panel") return t("panel.backup.panelAction");
  return t("panel.backup.dbAction");
});

/*
 * SEKME ARTIK GERÇEKTEN SEKME.
 *
 * ÖLÇÜLDÜ: "Yedek listesi" seçiliyken tam boy "Yeni yedek" formu da ekrandaydı — yani sekme
 * şeridi hiçbir şeyi ayırmıyordu; iki sekme aynı anda açıktı. Yan etkisi görsel değildi:
 * yaratma formunun kendi `.segmented-tabs` şeridi (site / abonelik / veritabanı / posta /
 * panel) üstteki ana şeritle AYNI ANDA görünüyordu, yani tek ekranda iki ayrı sekme çubuğu
 * vardı ve hangisinin neyi değiştirdiği belli değildi.
 *
 * HİÇBİR ŞEY KALDIRILMADI: form "Yeni yedek" sekmesinde, bir tık ötede duruyor ve o sekme
 * zaten şeridin ikinci düğmesi. Zincir/gözat panelleri açıkken gizleme koşulu da gereksizleşti
 * (o paneller yalnız "Liste" sekmesinde çiziliyor).
 */
const showCreateForm = computed(() => activeView.value === "create");

const siteName = ref(sitesStore.selectedSiteName);
const databaseEngine = ref("mariadb");
const databaseName = ref("");
const loading = ref(false);
const busy = ref(false);

/** Hold the same lock from confirmation until its result has been measured. */
async function withOperation(action: () => Promise<void>, writing = true) {
  if (!isActive() || busy.value || (writing && locked.value)) return;
  busy.value = true;
  // A read begun before this operation cannot republish the pre-operation catalogue.
  listRequest++;
  loading.value = false;
  try {
    await action();
  } catch (error) {
    if (isActive()) showError(error);
  } finally {
    if (isActive()) busy.value = false;
  }
}

type ReadOutcomeNotice = { message: string; failed: boolean; list: boolean; schedules: boolean; displayed: string };
let readOutcomeNotice: ReadOutcomeNotice | null = null;

function publishReadOutcome(outcome: ReadOutcomeNotice) {
  const errors = [outcome.list ? listLoad.message.value : "", outcome.schedules ? schedulesLoad.message.value : ""].filter(Boolean);
  outcome.displayed = [outcome.message, ...errors].filter(Boolean).join("\n\n");
  showNotice(outcome.displayed, outcome.failed || errors.length > 0);
}

/** Updating a read must retain the mutation's warning and not overwrite a newer notice. */
function updateReadOutcome() {
  if (!readOutcomeNotice || notice.value !== readOutcomeNotice.displayed) return false;
  publishReadOutcome(readOutcomeNotice);
  return true;
}

function reportWithListState(message: string, failed = false) {
  readOutcomeNotice = { message, failed, list: true, schedules: false, displayed: "" };
  publishReadOutcome(readOutcomeNotice);
}

async function retryFailedLoads() {
  if (!isActive() || busy.value || loading.value || schedulesLoading.value) return;
  const list = listErrorInNotice.value, schedules = schedulesErrorInNotice.value;
  if (schedules) await loadSchedules();
  if (list) await refresh();
}
const targetForm = reactive<BackupTargetPayload>({
  targetType: "local",
  remoteUrl: "",
  remoteUserName: "",
  remotePassword: "",
  remoteRegion: "",
  encryptionPassword: ""
});

// Artımsal seçenekler site ve abonelik yedeğinde ORTAK (sunucuda da ortak taban:
// `BackupArchiveRequest`), bu yüzden tek yerde tutulur.
const incrementalForm = reactive({ incremental: false, fullBackupEveryDays: 7 });
const subscriptionScope = reactive({
  includeFiles: true,
  includeDatabases: true,
  includeMail: true,
  includeFtp: true
});

// --- Abonelik seçimi ---
const subscriptionUserId = ref("");
const canPickSubscription = computed(() => isAdmin.value || isReseller.value);

/**
 * Yedeği alınabilecek abonelikler.
 *
 * Yönetici/bayi müşteri listesinden seçer (bayi zaten yalnız kendi müşterilerini görür); bayinin
 * ve müşterinin KENDİ aboneliği ayrıca başa eklenir. Alt-kullanıcılar liste dışıdır: abonelik
 * kaynakları ana hesapta toplanır, alt-kullanıcının ayrı bir aboneliği yoktur.
 */
const subscriptionChoices = computed(() => {
  const items: { id: string; label: string }[] = [];
  if (ownSubscriptionId.value) {
    items.push({
      id: ownSubscriptionId.value,
      label: t("panel.backup.subscription.own", { name: ownSubscriptionName.value || ownSubscriptionId.value })
    });
  }

  if (canPickSubscription.value) {
    for (const user of authStore.users) {
      if (user.role !== "Customer" || user.parentUserId) continue;
      if (normalizeId(user.id) === normalizeId(ownSubscriptionId.value)) continue;
      items.push({ id: user.id, label: user.userName });
    }
  }

  return items;
});

/** Guid'in tiresiz karşılaştırma biçimi (sunucu abonelik kimliğini "N" biçiminde döner). */
function normalizeId(value: string) {
  return value.replace(/-/g, "").toLowerCase();
}

/** Abonelik yedeğinin kaynağı ham kullanıcı kimliğidir; listede ADI gösterilir. */
function backupSourceLabel(backup: BackupRecord) {
  if (backup.type !== "subscription") {
    return backup.source;
  }

  const key = normalizeId(backup.subscriptionUserId || backup.source);
  if (key && normalizeId(ownSubscriptionId.value) === key) {
    return ownSubscriptionName.value || backup.source;
  }

  const user = authStore.users.find((item) => normalizeId(item.id) === key);
  return user?.userName || backup.source;
}

function backupKindLabel(type: string) {
  const key = `panel.backup.kind.${type}`;
  const translated = t(key);
  return translated === key ? type : translated;
}

// Panelin varsayılan yedek şifreleme ayarı (uç yalnız yöneticiye açık).
const encryption = ref<BackupEncryptionSettings | null>(null);
const encryptionForm = reactive({ password: "", confirm: "", acknowledged: false });
let encryptionRequest = 0;
let encryptionDraft = 0;
watch(encryptionForm, () => { encryptionDraft++; }, { flush: "sync" });

const encryptionMismatch = computed(() =>
  encryptionForm.confirm.length > 0 && encryptionForm.password !== encryptionForm.confirm);

// Kaydetme şartları: iki alan dolu ve EŞİT + "anladım" işaretli. Parola kaybı geri dönüşsüz
// olduğu için onay kutusu olmadan kaydetmeyiz.
const canSaveEncryption = computed(() =>
  encryptionForm.password.length > 0 &&
  encryptionForm.password === encryptionForm.confirm &&
  encryptionForm.acknowledged);

const encryptionStateKey = computed(() => {
  if (!encryption.value?.enabled) return "panel.status.disabled";
  return encryption.value.passwordUsable ? "panel.status.enabled" : "panel.backup.encryptionUnusable";
});

const encryptionTone = computed(() => {
  if (!encryption.value?.enabled) return "";
  return encryption.value.passwordUsable ? "ok" : "danger";
});

/*
 * Uzak hedefe ŞİFRESİZ gitme riski.
 *
 * Yönetici için kesin ölçülür (kayıtlı parola durumu elimizde). Kiracıda `GET /backup/encryption`
 * kapalı olduğundan kayıtlı parolanın varlığı BİLİNMEZ; bu yüzden uyarı metni "panelde kayıtlı
 * parola yoksa" koşuluyla yazılmıştır — iki durumda da doğrudur.
 */
const remoteUnencryptedRisk = computed(() =>
  targetForm.targetType !== "local" &&
  targetForm.encryptionPassword.length === 0 &&
  !(encryption.value?.enabled && encryption.value.passwordUsable));

// --- Granüler geri yükleme (zip içeriğine gözatma) ---
const browseRecord = ref<BackupRecord | null>(null);
const browsePrefix = ref("");
const browseEntries = ref<BackupEntry[]>([]);
const browseTruncated = ref(false);
const browseLoading = ref(false);
let browseRequest = 0;
onBeforeUnmount(() => { browseRequest++; });
const browseLoad = useSectionError();
const selectedItems = ref<string[]>([]);
const itemsTargetPath = ref("");
const restoreItemsBusy = ref(false);
const downloadItemsBusy = ref(false);
let selectedDownloadContext = 0;
watch(() => sitesStore.selectedSiteName, () => { selectedDownloadContext++; }, { flush: "sync" });
const canChooseItemsTarget = computed(() => isActive() && isAdmin.value);
const canRestoreItems = computed(() => isAdmin.value && canWrite() && !busy.value && !restoreItemsBusy.value && !downloadItemsBusy.value &&
  browseRecord.value?.format === "zip" && selectedItems.value.length > 0);
const canDownloadSelectedItems = computed(() => isActive() && !busy.value && !restoreItemsBusy.value && !downloadItemsBusy.value &&
  browseRecord.value?.format === "zip" && selectedItems.value.length > 0);

/**
 * Arşiv kökündeki girdilerin ÖNEK gruplaması.
 *
 * Abonelik arşivinde yollar `sites/<site>/files/…`, `databases/…`, `mail/<alan>.json`,
 * `ftp/accounts.json`; site arşivinde `files/…`, `databases/…`. Kök listesi bu gruplarla
 * anlamlanır. Alt dizinde gruplama YAPILMAZ: orada zaten tek bir bağlam vardır.
 */
const ENTRY_GROUPS: { key: string; prefixes: string[] }[] = [
  { key: "files", prefixes: ["files/", "sites/"] },
  { key: "databases", prefixes: ["databases/"] },
  { key: "mail", prefixes: ["mail/"] },
  { key: "ftp", prefixes: ["ftp/"] }
];

const groupedEntries = computed(() => {
  if (browsePrefix.value) {
    return [{ key: "", entries: browseEntries.value }];
  }

  const claimed = new Set<string>();
  const groups: { key: string; entries: BackupEntry[] }[] = [];
  for (const group of ENTRY_GROUPS) {
    const entries = browseEntries.value.filter((entry) =>
      group.prefixes.some((prefix) => entry.path.toLowerCase().startsWith(prefix)));
    for (const entry of entries) {
      claimed.add(entry.path);
    }
    if (entries.length) {
      groups.push({ key: group.key, entries });
    }
  }

  const other = browseEntries.value.filter((entry) => !claimed.has(entry.path));
  if (other.length) {
    groups.push({ key: "other", entries: other });
  }

  return groups;
});

const breadcrumbs = computed(() => {
  const parts = browsePrefix.value.split("/").filter(Boolean);
  return parts.map((name, index) => ({
    name,
    prefix: parts.slice(0, index + 1).join("/") + "/"
  }));
});

/**
 * Dropbox ve OneDrive'da adres YOKTUR, yalnızca hesap içindeki klasör yolu vardır. Kullanıcıyı
 * uydurma bir URL aramaya itmemek için etiket ve örnek metin bu iki hedefte değişir.
 */
function isFolderPathTarget(type: string) {
  return type === "dropbox" || type === "onedrive";
}

const remoteUrlPlaceholder = computed(() => {
  switch (targetForm.targetType) {
    case "ftp":
      return "ftp://backup.example.com/adapanel/";
    case "webdav":
      return "https://dav.example.com/backups/";
    case "dropbox":
    case "onedrive":
      return "/AdaPanel-yedekler";
    case "azure":
      return "https://hesap.blob.core.windows.net/yedekler/adapanel/";
    default:
      return "https://s3.eu-central-1.amazonaws.com/bucket-adi/yedekler/";
  }
});

/**
 * Kimlik alanlarının adı hedefe göre DEĞİŞİR. "Kullanıcı adı / Parola" demek, Dropbox jetonu
 * ya da Azure hesap anahtarı bekleyen bir kutuda kullanıcıyı yanlış değeri yapıştırmaya iter;
 * hata da ancak gece yedeği başarısız olunca görünür.
 */
function remoteUserLabel(type: string) {
  switch (type) {
    case "s3":
      return t("panel.backup.remoteAccessKey");
    case "azure":
      return t("panel.backup.remoteAzureAccount");
    case "dropbox":
    case "onedrive":
      return t("panel.backup.remoteClientId");
    default:
      return t("panel.backup.remoteUser");
  }
}

function remoteSecretLabel(type: string) {
  switch (type) {
    case "s3":
      return t("panel.backup.remoteSecretKey");
    case "azure":
      return t("panel.backup.remoteAzureKey");
    case "dropbox":
    case "onedrive":
      return t("panel.backup.remoteToken");
    default:
      return t("panel.backup.remotePassword");
  }
}

const cloudTargetHint = computed(() => {
  switch (targetForm.targetType) {
    case "dropbox":
      return t("panel.backup.target.dropboxHint");
    case "onedrive":
      return t("panel.backup.target.onedriveHint");
    case "azure":
      return t("panel.backup.target.azureHint");
    default:
      return "";
  }
});

// --- Yedek doğrulama (uzlaştırma) ---
const verifying = ref(false);
const verification = ref<BackupVerificationResult | null>(null);

/**
 * Kayıtların GERÇEKTEN durup durmadığını ölçer. Sonuç ekranda kalır: sayılar bir bildirim
 * balonunda gösterilseydi kullanıcı hangi yedeğin kayıp olduğunu göremezdi.
 */
async function verify() {
  if (!isActive() || verifying.value || busy.value) return;
  verifying.value = true;
  try {
    const result = await verifyBackups();
    if (!isActive()) return;
    verification.value = result;
    // Ölçüm kayıtlara işlendi (uzak durum); liste tazelenmezse rozetler eski kalırdı.
    await refresh();
  } catch (error) {
    if (isActive()) showError(error);
  } finally {
    if (isActive()) verifying.value = false;
  }
}

watch(() => sitesStore.selectedSiteName, (value) => {
  if (value) {
    siteName.value = value;
  }
});

// --- Artımsal zincir ---
const chainRecord = ref<BackupRecord | null>(null);
const chain = ref<BackupChain | null>(null);
const chainLoading = ref(false);
const chainLoad = useSectionError();

// --- Abonelik yedek programları ---
const schedules = ref<SubscriptionBackupSchedule[]>([]);
const schedulePager = usePagination(schedules);
const schedulesLoading = ref(false);
const schedulesLoaded = ref(false);
const schedulesLoad = useSectionError();
const schedulesErrorInNotice = computed(() => !!schedulesLoad.message.value && noticeError.value && notice.value.includes(schedulesLoad.message.value));
let schedulesRequest = 0;
let scheduleFormRequest = 0;
const scheduleFormLoading = ref(false);
const newScheduleUserId = ref("");

type ScheduleFormState = {
  userId: string;
  userName: string;
  planName: string;
  maxBackupsPerDay: number;
  remotePasswordSet: boolean;
  enabled: boolean;
  /** Ortak program biçimi; alanlar BackupScheduleFields ile düzenlenir. */
  plan: BackupSchedulePlan;
  retentionDays: number;
  keepCount: number;
  incremental: boolean;
  fullBackupEveryDays: number;
  includeFiles: boolean;
  includeDatabases: boolean;
  includeMail: boolean;
  includeFtp: boolean;
  targetType: string;
  remoteUrl: string;
  remoteUserName: string;
  remoteRegion: string;
  remotePassword: string;
};

const scheduleForm = ref<ScheduleFormState | null>(null);
const scheduleThrottled = ref(false);
let scheduleDraft = 0;
watch(scheduleForm, () => { scheduleDraft++; }, { deep: true, flush: "sync" });

/** Sunucudan gelen abonelik programını ortak biçime çevirir (liste satırı ve form aynı yerden okur). */
function scheduleToPlan(schedule: SubscriptionBackupSchedule): BackupSchedulePlan {
  return {
    mode: schedule.scheduleMode,
    intervalHours: schedule.intervalHours,
    hour: schedule.scheduleHour,
    minute: schedule.scheduleMinute,
    dayOfWeek: schedule.scheduleDayOfWeek,
    dayOfMonth: schedule.scheduleDayOfMonth,
    secondHour: schedule.scheduleSecondHour ?? null,
    secondMinute: schedule.scheduleSecondMinute ?? 0
  };
}

// Otomatik yedek programı (yalnız admin; motor = Otomasyon servisi, bu kart en sık kullanılan ayarların kısayolu).
const autoStatus = ref<AutomationStatus | null>(null);
const autoForm = ref<AutomationSettings | null>(null);
// Otomatik yedek ayarı OKUNAMADIYSA kart sessizce kaybolmaz; "okunamadı" durumu ayrı tutulur.
const autoLoad = useSectionError();
/**
 * Sunucu geneli programın ortak biçimi.
 *
 * Burada eskiden "günlük = 24 saat, haftalık = 168 saat" diye bir eşleme vardı: sıklık bir
 * SAATE değil bir ARALIĞA çevriliyordu ve yedeğin saati ilk tetiklenen ana kilitleniyordu.
 * Artık mod ve saat doğrudan taşınıyor; eşleme yok, dolayısıyla kayma da yok.
 */
const autoSchedule = reactive<BackupSchedulePlan>(bosProgram());
let autoRequest = 0;
let autoDraft = 0;
watch([autoForm, autoSchedule], () => { autoDraft++; }, { deep: true, flush: "sync" });
const autoServerZone = computed(
  () => autoStatus.value?.serverTimeZoneDisplayName || autoStatus.value?.serverTimeZoneId || ""
);
const autoServerLocalTime = computed(() => (autoStatus.value?.serverLocalTime || "").replace("T", " "));

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  if (!isActive()) return;
  await refresh();
  if (!isActive()) return;
  await loadSubscriptionChoices();
  if (!isActive()) return;
  if (isAdmin.value) {
    await loadAutoBackup();
    if (!isActive()) return;
    await loadEncryption();
    if (!isActive()) return;
    await loadBackupRoot();
  }

  // Sunucuda süren iş varsa şerit ekran açılır açılmaz dolar (yenilenmiş sayfa dahil).
  if (isActive()) await serverBackup.start();
});

/*
 * SUNUCUDAKİ İŞ BİTTİĞİNDE LİSTE TAZELENİR.
 *
 * Bu sekme işi kendisi başlatmadıysa (sayfa yenilenmişti) sonucu isteyecek açık bir isteği yok;
 * liste tazelenmezse yeni yedek ekranda HİÇ görünmez ve kullanıcı yedeğin alınmadığını sanır.
 */
watch(
  () => serverBackup.job.value,
  async (yeni, eski) => {
    if (eski && !yeni && !runningBackup.running.value) {
      await refresh();
    }
  });

/**
 * Yedek kök klasörünü Ayarlar tercihinden OKUR (uydurmaz).
 *
 * `GET /panel-settings` yalnız yöneticiye açık. Okunamazsa satır GÖSTERİLMEZ; yerine tahmini
 * bir yol basmak, kullanıcıyı var olmayan bir klasöre gönderirdi. Yedek listesi bu yüzden
 * etkilenmez — kök yolu bir kolaylıktır, listenin koşulu değildir.
 */
async function loadBackupRoot() {
  if (!isActive()) return;
  try {
    const preferences = await getPanelPreferences();
    if (!isActive()) return;
    backupRoot.value = preferences.defaultBackupRoot ?? "";
  } catch {
    if (isActive()) backupRoot.value = "";
  }
}

/**
 * Abonelik seçicisinin kaynakları.
 *
 * Yönetici/bayi müşteri listesini `/users`ten alır. Kapsamlı kullanıcı kendi abonelik KİMLİĞİNİ
 * `/subscriptions/me`den alır: oturumdaki kullanıcı kimliğini kullanmak alt-kullanıcıda yanlış
 * olurdu (alt-kullanıcının kendi aboneliği yoktur, ana hesabınkini paylaşır).
 */
async function loadSubscriptionChoices() {
  if (!isActive()) return;
  if (canPickSubscription.value && authStore.users.length === 0) {
    await authStore.fetchUsers().catch((error: unknown) => { if (isActive()) showError(error); });
  }
  if (!isActive()) return;
  if (!isAdmin.value) {
    try {
      const usage = await getMySubscription();
      if (!isActive()) return;
      ownSubscriptionId.value = usage.userId;
      ownSubscriptionName.value = usage.userName;
      if (!subscriptionUserId.value) {
        subscriptionUserId.value = usage.userId;
      }
      if (!newScheduleUserId.value) {
        newScheduleUserId.value = usage.userId;
      }
    } catch {
      // Abonelik kaydı yoksa seçici boş kalır ve ekranda sebebi yazan uyarı görünür;
      // uydurma bir kimlikle istek göndermek 403 üretirdi.
      if (isActive()) ownSubscriptionId.value = "";
    }
  }
}

async function loadEncryption() {
  if (!isActive() || !isAdmin.value || busy.value) return;
  const request = ++encryptionRequest;
  const isCurrent = () => isActive() && isAdmin.value && request === encryptionRequest;
  try {
    const settings = await getBackupEncryption();
    if (isCurrent()) encryption.value = settings;
  } catch (error) {
    if (!isCurrent()) return;
    // Kasa bu kurulumda yoksa (`backup.encryption_unavailable`) kart gizlenir; yedek listesi
    // etkilenmez. Sebep yine de şeritte söylenir — sessizce kaybolmaz.
    encryption.value = null;
    showError(error);
  }
}

async function saveEncryption() {
  if (!canAdminWrite() || !encryption.value || !canSaveEncryption.value) {
    return;
  }

  await withOperation(async () => {
    encryptionRequest++;
    const draft = encryptionDraft;
    const settings = await setBackupEncryption(encryptionForm.password);
    if (!isActive() || !isAdmin.value) return;
    encryption.value = settings;
    if (draft === encryptionDraft) resetEncryptionForm();
    showNotice(t("backup.encryption_saved"), false);
  });
}

async function disableEncryption() {
  if (!canAdminWrite() || !encryption.value) return;
  // Yıkıcı: bundan sonraki yedekler şifresiz alınır. MEVCUT şifreli yedekler yine parola ister —
  // onay metni bunu açıkça söylüyor.
  await withOperation(async () => {
    const draft = encryptionDraft;
    await confirm({
      title: t("panel.backup.encryptionDisableTitle"),
      message: t("panel.backup.encryptionDisableMessage"),
      confirmLabel: t("panel.backup.encryptionDisable"),
      action: async () => {
        if (!canAdminWrite()) return;
        encryptionRequest++;
        try {
          // Boş parola = kaydı sil + şifrelemeyi kapat (sunucu sözleşmesi).
          const settings = await setBackupEncryption("");
          if (!isActive() || !isAdmin.value) return;
          encryption.value = settings;
          if (draft === encryptionDraft) resetEncryptionForm();
          showNotice(t("backup.encryption_saved"), false);
        } catch (error) {
          if (isActive()) showError(error);
        }
      }
    });
  });
}

function resetEncryptionForm() {
  // Parola bellekte/ekranda ASILI KALMAZ.
  encryptionForm.password = "";
  encryptionForm.confirm = "";
  encryptionForm.acknowledged = false;
}

/*
 * "KAPALI" ile "OKUNAMADI" AYRI ŞEYLER.
 *
 * Eskiden hata sessizce yutuluyordu ve otomatik yedek kartı HİÇ ÇİZİLMİYORDU: kullanıcı panelde
 * zamanlanmış yedek diye bir özellik OLMADIĞINI sanıyordu. Üstelik en kötü hâli şuydu — yedeği
 * açık olan bir kurulumda kart kaybolduğu için kullanıcı "yedeğim yok" deyip elle yedek almaya
 * kalkıyordu. Artık okunamadıysa bunu SÖYLÜYORUZ ve tekrar deneme yolu bırakıyoruz.
 */
async function loadAutoBackup() {
  if (busy.value) return false;
  return readAutoBackup();
}

async function readAutoBackup() {
  if (!isActive() || !isAdmin.value) return false;
  const request = ++autoRequest;
  const draft = autoDraft;
  const isCurrent = () => isActive() && isAdmin.value && request === autoRequest;
  try {
    const status = await getAutomation();
    if (!isCurrent()) return false;
    autoStatus.value = status;
    if (draft === autoDraft) applyAutoSettings(autoStatus.value.settings);
    autoLoad.clear();
    return true;
  } catch (error) {
    if (isCurrent()) {
      autoStatus.value = null;
      autoForm.value = null;
      autoLoad.capture(error);
    }
    return false;
  }
}

/** Sunucudan gelen (normalize edilmiş) ayarları forma ve ortak program biçimine yazar. */
function applyAutoSettings(settings: AutomationSettings) {
  autoForm.value = { ...settings, backupDatabaseEngines: [...(settings.backupDatabaseEngines ?? [])] };
  Object.assign(autoSchedule, {
    mode: settings.backupScheduleMode,
    intervalHours: settings.backupIntervalHours,
    hour: settings.backupScheduleHour,
    minute: settings.backupScheduleMinute,
    dayOfWeek: settings.backupScheduleDayOfWeek,
    dayOfMonth: settings.backupScheduleDayOfMonth,
    secondHour: settings.backupScheduleSecondHour ?? null,
    secondMinute: settings.backupScheduleSecondMinute ?? 0
  });
}

async function saveAutoBackup() {
  if (!canAdminWrite() || !autoForm.value) return;
  await withOperation(async () => {
    autoRequest++;
    const draft = autoDraft;
    // backupRemotePassword boş = sunucudaki kayıtlı parola korunur (Otomasyon ekranıyla aynı sözleşme).
    const status = await updateAutomation({
      ...autoForm.value!,
      backupDatabaseEngines: [...autoForm.value!.backupDatabaseEngines],
      backupScheduleMode: autoSchedule.mode,
      backupIntervalHours: autoSchedule.intervalHours,
      backupScheduleHour: autoSchedule.hour,
      backupScheduleMinute: autoSchedule.minute,
      backupScheduleDayOfWeek: autoSchedule.dayOfWeek,
      backupScheduleDayOfMonth: autoSchedule.dayOfMonth,
      backupScheduleSecondHour: autoSchedule.secondHour ?? null,
      backupScheduleSecondMinute: autoSchedule.secondMinute ?? 0,
      backupRemotePassword: ""
    });
    if (!isActive() || !isAdmin.value) return;
    autoStatus.value = status;
    if (draft === autoDraft) applyAutoSettings(status.settings);
    showNotice(t("panel.backup.autoSaved"), false);
  });
}

async function refresh() {
  if (!isActive()) return false;
  const request = ++listRequest;
  const isCurrent = () => isActive() && request === listRequest;
  loading.value = true;
  listConfirmed.value = false;
  try {
    const rows = await getBackups();
    if (!isCurrent()) return false;
    backups.value = rows;
    listConfirmed.value = true;
    const previousError = listLoad.message.value;
    listLoad.clear();
    fullRestore.clearRefreshError();
    if (!updateReadOutcome() && noticeError.value && notice.value === previousError) showNotice("");
    return true;
  } catch (error) {
    if (!isCurrent()) return false;
    // Liste ÇEKİLEMEDİ: eski içerik silinip "yedek yok" gösterilmez.
    listLoad.capture(error);
    if (!updateReadOutcome()) showError(error);
    return false;
  } finally {
    if (isCurrent()) loading.value = false;
  }
}

function toggleWarnings(backupId: string) {
  expandedWarnings.value = expandedWarnings.value === backupId ? "" : backupId;
}

async function createSelectedBackup() {
  if (!canWrite() || busy.value) return;
  if (!isAdmin.value && backupKind.value !== "site" && backupKind.value !== "subscription") return;
  if (backupKind.value === "site") {
    await createSiteBackup();
    return;
  }

  if (backupKind.value === "subscription") {
    await createSubscriptionBackup();
    return;
  }

  if (backupKind.value === "mail") {
    const target = buildTargetPayload();
    const label = runningLabel();
    await withOperation(async () => {
      // Yıkıcı değil (yalnız servis kısa süre durur) → nötr ton: kırmızı düğme ve "geri alınamaz" uyarısı yok.
      const approved = await confirm({
        title: t("panel.confirm.mailBackupTitle"),
        message: t("panel.backup.confirmMailBackup"),
        tone: "neutral",
        confirmLabel: t("panel.backup.mailAction"),
      });
      if (!approved || !canAdminWrite()) return;
      await performBackup(() => backupMail(target), label, target.encryptionPassword);
    });
    return;
  }

  if (backupKind.value === "panel") {
    await runBackup(() => backupPanel(buildTargetPayload()));
    return;
  }

  await createDatabaseBackup();
}

async function createSiteBackup() {
  if (!siteName.value) {
    showNotice(t("panel.php.siteRequired"), true);
    return;
  }

  await runBackup(() => backupSite(
    siteName.value,
    { incremental: incrementalForm.incremental, fullBackupEveryDays: incrementalForm.fullBackupEveryDays },
    buildTargetPayload()));
}

async function createSubscriptionBackup() {
  if (!subscriptionUserId.value) {
    showNotice(t("panel.backup.subscription.required"), true);
    return;
  }

  await runBackup(() => backupSubscription(
    subscriptionUserId.value,
    { ...subscriptionScope, ...incrementalForm },
    buildTargetPayload()));
}

async function createDatabaseBackup() {
  if (!canAdminWrite()) return;
  await runBackup(() => backupDatabase(databaseEngine.value, databaseName.value, buildTargetPayload()));
}

async function createDatabaseToConfiguredFtp() {
  if (!canAdminWrite()) return;
  const engine = databaseEngine.value;
  const name = databaseName.value;
  const encryptionPassword = targetForm.encryptionPassword;
  const label = runningLabel();
  await withOperation(async () => {
    const { settings } = await getAutomation();
    if (!canAdminWrite()) return;
    if (settings.backupTargetType !== 'ftp' || !settings.backupRemoteUrl) {
      showNotice(t('backup.retry_target_changed'), true);
      return;
    }
    const target = settings.backupRemoteUrl;
    await confirm({
      title: t('panel.backup.createToConfiguredFtp'),
      message: t('panel.backup.createToConfiguredFtpConfirm'),
      impact: [`${engine}: ${name || t('panel.backup.allDatabases')}`, target],
      tone: 'neutral',
      confirmLabel: t('panel.backup.createToConfiguredFtp'),
      action: async () => {
        if (!canAdminWrite()) return;
        await performBackup(() => backupDatabase(engine, name, {
          useConfiguredFtpTarget: true, remoteUrl: target, encryptionPassword
        }), label, encryptionPassword);
      }
    });
  });
}

/** Süren yedeğin şeritte yazacağı metin: NE alınıyor (tür + kaynak). */
function runningLabel() {
  if (backupKind.value === "site") {
    return `${t("panel.backup.running.creating")}: ${t("panel.backup.kind.site")} — ${siteName.value}`;
  }

  if (backupKind.value === "subscription") {
    const choice = subscriptionChoices.value.find((item) => item.id === subscriptionUserId.value);
    return `${t("panel.backup.running.creating")}: ${t("panel.backup.kind.subscription")} — ${choice?.label ?? subscriptionUserId.value}`;
  }

  if (backupKind.value === "database") {
    return `${t("panel.backup.running.creating")}: ${t("panel.backup.kind.database")} — ${databaseName.value || t("panel.backup.allDatabases")}`;
  }

  return `${t("panel.backup.running.creating")}: ${backupKindLabel(backupKind.value)}`;
}

async function runBackup(operation: () => Promise<BackupRecord>) {
  await withOperation(() => performBackup(operation, runningLabel(), targetForm.encryptionPassword));
}

async function performBackup(operation: () => Promise<BackupRecord>, label: string, submittedPassword: string) {
  if (!canWrite()) return;
  // Yedek uçları işi ARKA PLANA ALMIYOR: istek uçuştayken yedek gerçekten sürüyor, bu yüzden
  // şeritte gösterilen süre işin gerçek süresidir.
  runningBackup.begin(label);
  try {
    const created = await operation();
    if (!isActive()) return;
    await refresh();
    if (!isActive()) return;
    search.value = "";
    pager.reset();
    const closed = new Set(collapsedDays.value);
    closed.delete(localDayKey(created.createdAtUtc));
    collapsedDays.value = closed;
    activeView.value = "list";
    // Bu yedeğe özel parola tek seferliktir; formda asılı kalıp bir sonraki yedeğe sessizce
    // taşınmaz (kullanıcı hangi parolayla şifrelediğini karıştırmasın).
    if (targetForm.encryptionPassword === submittedPassword) targetForm.encryptionPassword = "";
    reportCreateResult(created);
  } catch (error) {
    if (isActive()) showError(error);
  } finally {
    if (isActive()) runningBackup.end();
  }
}

/**
 * Yedek ALINIRKEN atlanan öğeler sessizce yutulmaz.
 *
 * `created_with_errors` durumunda arşiv OLUŞTU ama içine giremeyen öğeler var; yeşil "tamam"
 * göstermek, olmayan bir veritabanının geri yükleme anında keşfedilmesi demektir.
 */
function reportCreateResult(record: BackupRecord) {
  const warnings = record.warnings ?? [];
  if (warnings.length === 0) {
    reportWithListState(t("panel.backup.created"));
    return;
  }

  expandedWarnings.value = record.id;
  reportWithListState([
    t(record.status === "created_with_errors" ? "panel.backup.createdWithErrors" : "panel.backup.createdWithWarnings"),
    `${t("panel.backup.warningsListTitle", { name: record.name })}:`,
    warningText(record)
  ].join("\n"), true);
}

/**
 * Geri yükleme.
 *
 * Artımsal halkada zincir ÖNCE ölçülür: sunucu kopuk zinciri zaten `backup.chain_broken` ile
 * reddeder, ama kullanıcıyı yüksek riskli onaydan geçirip sonra reddetmek, ona ne olduğunu
 * anlatmayan bir yol olurdu. Kopuksa zincir paneli açılır ve eksik halkanın ADI görünür.
 */
async function restore(backup: BackupRecord, chainChecked = false) {
  backup = { ...backup };
  await withOperation(async () => {
    if (!chainChecked && backup.chainId) {
      // Zinciri ÖLÇEMEDİYSEK de devam etmeyiz: kopuk bir zinciri geri yüklemeye başlamak, canlı
      // içeriğin üzerine eksik bir durum yazmak demektir. Sebep şeritte yazar, kullanıcı yineleyebilir.
      let resolved: BackupChain;
      try {
        resolved = await getBackupChain(backup.id, backup.chainId);
        if (!canWrite()) return;
      } catch (error) {
        if (isActive()) showError(error);
        return;
      }

      // Zincir SAĞLAMSA panel durumuna dokunulmaz (kullanıcı onu açmadı); kopuksa panel bu kayda
      // bağlanır ve eksik halkanın ADI ekranda görünür.
      if (resolved.broken) {
        chainRequest++;
        chainRecord.value = backup;
        chain.value = resolved;
        chainLoad.clear();
        closeBrowser();
        activeView.value = "list";
        showNotice(
          `${t("panel.backup.chain.broken")} — ${t("panel.backup.chain.missing")}: ${resolved.missingLinks.join(", ")}`,
          true);
        return;
      }
    }

    await fullRestore.restore(backup, reportRestoreResult, showError, refresh);
  });
}

/**
 * Şifreli yedeğin parolasını sorar. Boş bırakılabilir: sunucu o zaman panelde KAYITLI parolayı
 * dener. İptalde `null` döner (akış durur).
 */
async function askBackupPassword() {
  return prompt({
    title: t("panel.backup.restorePasswordTitle"),
    message: t("panel.backup.encryptionLossWarning"),
    label: t("panel.backup.encryptionPassword"),
    hint: t("panel.backup.restorePasswordHint"),
    password: true,
    allowEmpty: true,
    confirmLabel: t("panel.backup.restore")
  });
}

/**
 * Geri yükleme sonucunu OLDUĞU GİBİ söyler.
 *
 * NEDEN: sunucu yarım kalan adımları `warnings[]` ile döndürüyor ve durumu
 * `restored_with_errors` yapıyor. Koşulsuz "geri yüklendi" demek, kullanıcının elinde ne eski
 * hâl ne de dönmüş veritabanı varken ona "tamam" dedirtirdi. Üzerine yazılan içeriğin taşındığı
 * klasör (`preservedPath`) de söylenir: "yanlış yedeği geri yükledim" denince dönülecek tek yer orası.
 */
function reportRestoreResult(record: BackupRecord) {
  const report = restoreResult(record, t);
  // The result card reports a catalogue refresh failure separately from restoration.
  readOutcomeNotice = null;
  showNotice(t(report.failed ? "panel.backup.restoredWithErrors" : "panel.backup.restored"), report.failed);
}

/** Tek bir uyarının çevrilmiş satırı ("hedef: sebep"). */
function warningLine(warning: BackupRestoreWarning) {
  const translated = t(warning.messageKey, { target: warning.target });
  const message = translated === warning.messageKey ? warning.messageKey : translated;
  // Anahtar `{target}` içermiyorsa hedef kaybolmasın diye başa yazılır.
  return message.includes(warning.target) || !warning.target ? message : `${warning.target}: ${message}`;
}

/** Kayıt üzerindeki uyarıların çevrilmiş özeti (bildirim şeridi için). */
function warningText(backup: BackupRecord) {
  return (backup.warnings ?? []).map((warning) => warningLine(warning)).join("\n");
}

// Onay kutusundaki "etkilenecekler" listesi: ekranın zaten elinde olan yedek bilgileri.
function backupImpact(backup: BackupRecord) {
  const rows = [
    `${t("panel.backup.name")}: ${backup.name}`,
    `${t("panel.backup.type")}: ${backupKindLabel(backup.type)} (${backupSourceLabel(backup)})`,
    `${t("panel.backup.size")}: ${formatBytes(backup.size)}`,
    `${t("panel.backup.createdAt")}: ${new Date(backup.createdAtUtc).toLocaleString(getFormattingLocale())}`
  ];

  if (backup.chainId) {
    rows.push(backup.backupMode === "incremental"
      ? t("panel.backup.chain.linkBadge", { index: backup.chainIndex })
      : t("panel.backup.chain.headBadge"));
  }

  return rows;
}

// =====================================================================================
// ZİNCİR
// =====================================================================================

async function openChain(backup: BackupRecord) {
  if (!isActive() || busy.value) return;
  closeBrowser();
  activeView.value = "list";
  await loadChain(backup);
}

function closeChain() {
  chainRequest++;
  chainRecord.value = null;
  chain.value = null;
  chainLoading.value = false;
  chainLoad.clear();
}

async function reloadChain() {
  if (chainRecord.value) {
    await loadChain(chainRecord.value);
  }
}

/**
 * Zinciri çözer ve paneli BU kayda bağlar.
 *
 * Panelin kaydı ile gösterilen zincir HER ZAMAN aynı yedeğe aittir: ikisini ayrı ayrı set etmek,
 * A yedeğinin başlığı altında B yedeğinin zincirini göstermeye açık kapı bırakıyordu.
 */
async function loadChain(backup: BackupRecord) {
  if (!isActive()) return null;
  const request = ++chainRequest;
  const isCurrent = () => isActive() && request === chainRequest;
  chainRecord.value = { ...backup };
  chain.value = null;
  chainLoading.value = true;
  try {
    const result = await getBackupChain(backup.id, backup.chainId);
    if (!isCurrent()) return null;
    chain.value = result;
    chainLoad.clear();
    return chain.value;
  } catch (error) {
    if (!isCurrent()) return null;
    chain.value = null;
    chainLoad.capture(error);
    return null;
  } finally {
    if (isCurrent()) chainLoading.value = false;
  }
}

const chainDeletionPlan = computed(() => chainRecord.value
  ? getBackupDeletionPlan(backups.value, chainRecord.value)
  : { targets: [], ambiguous: false });

/** Only actual descendants belong to the selected branch's destructive impact. */
function chainDeleteTargets(backup: BackupRecord) {
  return getBackupDeletionPlan(backups.value, backup).targets;
}

async function retryRemote(backup: BackupRecord) {
  if (!canAdminWrite()) return;
  backup = { ...backup };
  await withOperation(async () => {
    if (!await readAutoBackup()) {
      if (isActive() && autoLoad.message.value) showNotice(autoLoad.message.value, true);
      return;
    }
    if (!canAdminWrite()) return;
    if (!autoForm.value || autoForm.value.backupTargetType !== 'ftp' || !autoForm.value.backupRemoteUrl) {
      showNotice(t('backup.retry_target_changed'), true);
      return;
    }
    const target = autoForm.value.backupRemoteUrl;
    await confirm({
      title: t('panel.backup.retryRemote'),
      message: t('panel.backup.retryRemoteConfirm'),
      tone: 'neutral',
      confirmLabel: t('panel.backup.retryRemote'),
      impact: [backup.name, target],
      action: async () => {
        if (!canAdminWrite()) return;
        try {
          await retryRemoteUpload(backup.id, target);
          if (!isActive()) return;
          await refresh();
          if (isActive()) reportWithListState(t('backup.remote_retried'));
        } catch (error) { if (isActive()) showError(error); }
      }
    });
  });
}

async function removeBackup(backup: BackupRecord) {
  backup = { ...backup };
  // 409 "bağımlı halka var" sonucunda İKİNCİ bir onay gerekir; onay diyaloğu aynı anda tek
  // örnektir, bu yüzden zincir onayı ilk diyalog KAPANDIKTAN sonra açılır.
  let dependentsRejected = false;
  let dependentsDetail = "";

  await withOperation(async () => {
    await confirm({
      title: t("panel.confirm.deleteBackupTitle", { name: backup.name }),
      message: t("panel.backup.confirmDelete", { name: backup.name }),
      impact: backupImpact(backup),
      action: async () => {
        if (!canWrite()) return;
        try {
          await deleteBackup(backup.id);
          if (!isActive()) return;
          await refresh();
          if (!isActive()) return;
          if (browseRecord.value?.id === backup.id) {
            closeBrowser();
          }
          if (chainRecord.value?.id === backup.id) {
            closeChain();
          }
          reportWithListState(t("panel.backup.deletedNotice"));
        } catch (error) {
          if (!isActive()) return;
          if (extractApiMessage(error) === "backup.chain_has_dependents") {
            // Otomatik olarak zincir silmeye GEÇMEYİZ: kullanıcı ne kaybedeceğini görmeli.
            // Ayrıntı (bağımlı halkaların adları) boş gelse bile akış devam eder; kaybedilecek
            // kayıtları ekran zaten kendi listesinden de çözebiliyor.
            dependentsRejected = true;
            dependentsDetail = extractApiDetails(error);
            return;
          }
          showError(error);
        }
      },
    });

    // Sunucu 409 verdiyse kullanıcı zinciri görerek ikinci bir karar verir.
    if (dependentsRejected && canWrite()) {
      await confirmRemoveChain(backup, dependentsDetail);
    }
  });
}

/**
 * Yedeği VE ona bağımlı halkaları siler. YIKICI ve geri dönüşsüz olduğu için yedek adının
 * yazılması istenir; kaç yedeğin gideceği ve adları onay kutusunda listelenir.
 */
async function removeChain(backup: BackupRecord, dependentsDetail = "") {
  backup = { ...backup };
  await withOperation(() => confirmRemoveChain(backup, dependentsDetail));
}

async function confirmRemoveChain(backup: BackupRecord, dependentsDetail: string) {
  const plan = getBackupDeletionPlan(backups.value, backup);
  if (plan.ambiguous) {
    showNotice(t("backup.chain_ambiguous"), true);
    return;
  }
  const targets = plan.targets.map(({ id, name }) => ({ id, name }));
  const expectedBackupIds = targets.map(item => item.id);
  const impact = targets.map((item) => item.name);
  if (dependentsDetail) {
    impact.push(dependentsDetail);
  }

  await confirm({
    title: t("panel.backup.chain.deleteTitle", { count: targets.length }),
    message: dependentsDetail
      ? `${t("backup.chain_has_dependents")}\n\n${t("panel.backup.chain.deleteMessage", { count: targets.length })}`
      : t("panel.backup.chain.deleteMessage", { count: targets.length }),
    impact,
    requireText: backup.name,
    confirmLabel: t("panel.backup.chain.delete"),
    action: async () => {
      if (!canWrite()) return;
      try {
        const result = await deleteBackupChain(backup.id, expectedBackupIds);
        if (!isActive()) return;
        await refresh();
        if (!isActive()) return;
        if (chainRecord.value && result.deletedIds.includes(chainRecord.value.id)) {
          closeChain();
        }
        if (browseRecord.value && result.deletedIds.includes(browseRecord.value.id)) {
          closeBrowser();
        }
        const deletedIds = new Set(result.deletedIds);
        const remaining = targets.filter(item => !deletedIds.has(item.id));
        if (remaining.length > 0) {
          reportWithListState(t("panel.backup.chain.partialDeleted", {
            count: deletedIds.size,
            names: remaining.map(item => item.name).join(", ")
          }), true);
        } else {
          reportWithListState(t("panel.backup.chain.deletedNotice", {
            count: deletedIds.size,
            names: result.deletedNames.join(", ")
          }), false);
        }
      } catch (error) {
        if (!isActive()) return;
        if (extractApiMessage(error) === "backup.chain_changed") {
          await refresh();
          if (isActive()) reportWithListState(t("backup.chain_changed"), true);
        } else {
          showError(error);
        }
      }
    },
  });
}

async function download(backup: BackupRecord) {
  const id = backup.id, name = backup.name;
  await withOperation(() => downloadBackup(id, name, isActive), false);
}

function buildTargetPayload() {
  return { ...targetForm };
}

// =====================================================================================
// GÖZAT + SEÇEREK GERİ YÜKLE
// =====================================================================================

async function openBrowser(backup: BackupRecord) {
  if (!isActive() || (busy.value && !browseLoading.value)) return;
  closeBrowser();
  browseRecord.value = backup;
  closeChain();
  activeView.value = "list";
  selectedItems.value = [];
  itemsTargetPath.value = "";
  await navigateTo("");
}

function closeBrowser() {
  browseRequest++;
  if (browseLoading.value) busy.value = false;
  browseLoading.value = false;
  browsePrefix.value = "";
  browseTruncated.value = false;
  browseRecord.value = null;
  browseEntries.value = [];
  selectedItems.value = [];
  browseLoad.clear();
}

async function navigateTo(prefix: string) {
  if (!isActive() || !browseRecord.value || (busy.value && !browseLoading.value)) return;
  const recordId = browseRecord.value.id;
  const request = ++browseRequest;
  const isCurrent = () => isActive() && request === browseRequest && browseRecord.value?.id === recordId;
  browsePrefix.value = prefix;
  browseEntries.value = [];
  browseTruncated.value = false;
  browseLoading.value = true;
  busy.value = true;
  try {
    const result = await getBackupEntries(recordId, prefix);
    if (!isCurrent()) return;
    browsePrefix.value = result.prefix;
    browseEntries.value = result.entries;
    browseTruncated.value = result.truncated;
    browseLoad.clear();
  } catch (error) {
    if (!isCurrent()) return;
    /*
     * Gözatma ucu parola ALMAZ (bilerek: parolayı sorgu dizesine koymak onu IIS erişim
     * günlüklerine ve tarayıcı geçmişine yazmak olurdu). Şifreli yedekte gözatma yalnız panelde
     * KAYITLI parola varsa çalışır — genel "parolayı girin" metni burada yanlış yol gösterirdi.
     */
    browseEntries.value = [];
    if (extractApiMessage(error) === "backup.password_required") {
      browseLoad.message.value = t("panel.backup.browseEncryptedHint");
    } else {
      browseLoad.capture(error);
    }
  } finally {
    if (isCurrent()) {
      browseLoading.value = false;
      busy.value = false;
    }
  }
}

/**
 * SEÇİLEN öğeleri geri yükler ve sonucu DÜRÜST raporlar.
 *
 * Yarıda hata alınırsa önceki dosyalar diskte kalmış olabilir. Aynı yazmayı yeniden
 * göndermek sonuç ölçmek değildir: otomatik tekrar yapılmaz, belirsizlik bildirilir.
 */
async function downloadSelectedItems() {
  if (!canDownloadSelectedItems.value || !browseRecord.value) return;
  const record = { ...browseRecord.value }, items = [...selectedItems.value];
  const request = browseRequest, context = selectedDownloadContext;
  const current = () => isActive() && request === browseRequest && context === selectedDownloadContext && browseRecord.value?.id === record.id;
  await withOperation(async () => {
    downloadItemsBusy.value = true;
    let password = "";
    try {
      if (record.encrypted) {
        const entered = await askBackupPassword();
        if (entered === null || !current()) return;
        password = entered;
      }
      if (!current()) return;
      await downloadBackupItems(record.id, items, password, current);
      if (current()) showNotice(t("panel.backup.selectedDownloadStarted"));
    } catch (error) {
      if (current()) showError(error);
    } finally {
      password = "";
      if (isActive()) downloadItemsBusy.value = false;
    }
  }, false);
}

async function restoreSelectedItems() {
  const record = browseRecord.value;
  if (!canRestoreItems.value || !record) return;
  const request = browseRequest, epoch = getSessionEpoch();
  const isCurrent = () => canWrite() && request === browseRequest && browseRecord.value?.id === record.id && epoch === getSessionEpoch();
  const items = [...selectedItems.value];
  const targetPath = canChooseItemsTarget.value ? itemsTargetPath.value : "";

  await withOperation(async () => {
    // Şifreli yedekte parola önce sorulur (gözatma kayıtlı parolayla çalışmış olabilir, ama
    // geri yükleme ucu isteğe ait parolayı da kabul eder).
    let password = "";
    if (record.encrypted) {
      const entered = await askBackupPassword();
      if (entered === null || !isCurrent()) {
        return;
      }

      password = entered;
    }

    const impact = [
      `${t("panel.backup.name")}: ${record.name}`,
      `${t("panel.backup.restoreSelected")}: ${items.length}`,
      `${t("panel.backup.itemsTarget")}: ${targetPath || t("panel.backup.itemsTargetOriginal")}`
    ];

    // A custom target can overwrite files; the server-selected folder is a separate extraction.
    await confirm({
      title: targetPath ? t("panel.confirm.restoreBackupTitle", { name: record.name }) : t("panel.backup.extractTitle"),
      message: t(targetPath ? "panel.backup.confirmRestoreItems" : "panel.backup.confirmExtractItems"),
      tone: targetPath ? "danger" : "neutral",
      impact,
      requireText: targetPath ? record.name : undefined,
      confirmLabel: t(targetPath ? "panel.backup.restore" : "panel.backup.restoreSelected"),
      busyLabel: t(targetPath ? "panel.backup.restoring" : "panel.backup.extracting"),
      action: async () => {
        if (!isCurrent() || restoreItemsBusy.value) return;
        restoreItemsBusy.value = true;
        try {
          const result = await restoreBackupItems(record.id, items, targetPath, password);
          if (!isCurrent()) return;
          if (selectedItems.value.length === items.length && selectedItems.value.every((item, index) => item === items[index])) selectedItems.value = [];
          showNotice(t(result.targetRoot.trim() ? "panel.backup.itemsRestoredAll" : "panel.backup.itemsRestoredScoped", {
            items: items.length,
            count: result.restoredCount,
            target: result.targetRoot
          }), false);
        } catch (error) {
          if (isCurrent()) showNotice(`${resolveApiMessage(error)}\n\n${t("panel.backup.itemsRestoreUnconfirmed")}`, true);
        } finally {
          if (isActive()) restoreItemsBusy.value = false;
          password = "";
        }
      },
    });
    password = "";
  });
}

// =====================================================================================
// ABONELİK YEDEK PROGRAMLARI
// =====================================================================================

async function openSchedules() {
  if (!isActive()) return;
  activeView.value = "schedules";
  if (!schedulesLoaded.value) {
    await loadSchedules();
  }
}

async function loadSchedules() {
  if (busy.value) return false;
  return readSchedules();
}

async function readSchedules() {
  if (!isActive()) return false;
  const request = ++schedulesRequest;
  const isCurrent = () => isActive() && request === schedulesRequest;
  schedulesLoading.value = true;
  try {
    const rows = await getBackupSchedules();
    if (!isCurrent()) return false;
    schedules.value = rows;
    schedulesLoaded.value = true;
    schedulesLoad.clear();
    updateReadOutcome();
    return true;
  } catch (error) {
    if (isCurrent()) {
      schedulesLoaded.value = false;
      schedulesLoad.capture(error);
      updateReadOutcome();
    }
    return false;
  } finally {
    if (isCurrent()) schedulesLoading.value = false;
  }
}

/** Kayıtlı programı (yoksa VARSAYILANLARI) forma alır. */
async function openScheduleForm(userId: string) {
  if (!isActive() || (busy.value && !scheduleFormLoading.value)) return;
  if (!userId) {
    showNotice(t("panel.backup.subscription.required"), true);
    return;
  }

  const request = ++scheduleFormRequest;
  const isCurrent = () => isActive() && request === scheduleFormRequest;
  scheduleFormLoading.value = true;
  busy.value = true;
  try {
    const dto = await getBackupSchedule(userId);
    if (isCurrent()) applyScheduleToForm(dto);
  } catch (error) {
    if (isCurrent()) {
      scheduleForm.value = null;
      showError(error);
    }
  } finally {
    if (isCurrent()) {
      busy.value = false;
      scheduleFormLoading.value = false;
    }
  }
}

function closeScheduleForm() {
  scheduleFormRequest++;
  scheduleForm.value = null;
  if (scheduleFormLoading.value) busy.value = false;
  scheduleFormLoading.value = false;
}

function invalidateSchedulesRead() {
  schedulesRequest++;
  schedulesLoading.value = false;
}

function reportScheduleResult(message: string, failed = false, includeList = false) {
  readOutcomeNotice = { message, failed, list: includeList, schedules: true, displayed: "" };
  publishReadOutcome(readOutcomeNotice);
}

function applyScheduleToForm(dto: SubscriptionBackupSchedule) {
  scheduleThrottled.value = dto.throttledByQuota;
  scheduleForm.value = {
    userId: dto.userId,
    userName: dto.userName,
    planName: dto.planName,
    maxBackupsPerDay: dto.maxBackupsPerDay,
    remotePasswordSet: dto.remotePasswordSet,
    enabled: dto.enabled,
    // KOTAYA ÇEKİLMİŞ değer: formu istenen değerle doldurmak, kabul edilmemiş bir sıklığı
    // kayıtlı göstermek olurdu.
    plan: reactive(scheduleToPlan(dto)),
    retentionDays: dto.retentionDays,
    keepCount: dto.keepCount,
    incremental: dto.incremental,
    fullBackupEveryDays: dto.fullBackupEveryDays,
    includeFiles: dto.includeFiles,
    includeDatabases: dto.includeDatabases,
    includeMail: dto.includeMail,
    includeFtp: dto.includeFtp,
    targetType: dto.targetType,
    remoteUrl: dto.remoteUrl,
    remoteUserName: dto.remoteUserName,
    remoteRegion: dto.remoteRegion,
    // Parola sunucudan ASLA dönmez; boş gönderim "değiştirme" demektir.
    remotePassword: ""
  };
}

async function saveSchedule() {
  const form = scheduleForm.value;
  if (!form) return;

  await withOperation(async () => {
    invalidateSchedulesRead();
    const draft = scheduleDraft;
    const dto = await updateBackupSchedule(form.userId, {
      enabled: form.enabled,
      scheduleMode: form.plan.mode,
      intervalHours: form.plan.intervalHours,
      scheduleHour: form.plan.hour,
      scheduleMinute: form.plan.minute,
      scheduleDayOfWeek: form.plan.dayOfWeek,
      scheduleDayOfMonth: form.plan.dayOfMonth,
      scheduleSecondHour: form.plan.secondHour ?? null,
      scheduleSecondMinute: form.plan.secondMinute ?? 0,
      retentionDays: form.retentionDays,
      keepCount: form.keepCount,
      incremental: form.incremental,
      fullBackupEveryDays: form.fullBackupEveryDays,
      includeFiles: form.includeFiles,
      includeDatabases: form.includeDatabases,
      includeMail: form.includeMail,
      includeFtp: form.includeFtp,
      targetType: form.targetType,
      remoteUrl: form.remoteUrl,
      remoteUserName: form.remoteUserName,
      remoteRegion: form.remoteRegion,
      remotePassword: form.remotePassword
    });

    if (!isActive()) return;
    if (draft === scheduleDraft && scheduleForm.value === form) applyScheduleToForm(dto);
    await readSchedules();
    if (!isActive()) return;

    if (dto.throttledByQuota) {
      // Kaydedildi AMA istenen sıklıkla değil: sebebi (plan kotası) söylenmeden "kaydedildi"
      // demek, kullanıcının kurmadığı bir programı kurdu sanmasıdır.
      reportScheduleResult(t("panel.backup.schedule.throttled", { hours: dto.intervalHours }), true);
      return;
    }

    reportScheduleResult(t("backup.schedule_saved"));
  });
}

async function removeSchedule(schedule: SubscriptionBackupSchedule) {
  schedule = { ...schedule };
  await withOperation(async () => {
    invalidateSchedulesRead();
    await confirm({
      title: t("panel.backup.schedule.removeTitle"),
      message: t("panel.backup.schedule.removeMessage"),
      impact: [schedule.userName || schedule.userId],
      confirmLabel: t("panel.backup.schedule.remove"),
      action: async () => {
        if (!canWrite()) return;
        try {
          await deleteBackupSchedule(schedule.userId);
          if (!isActive()) return;
          if (scheduleForm.value?.userId === schedule.userId) {
            closeScheduleForm();
          }
          await readSchedules();
          if (isActive()) reportScheduleResult(t("backup.schedule_deleted"));
        } catch (error) {
          if (isActive()) showError(error);
        }
      },
    });
  });
}

async function runScheduleNow(schedule: SubscriptionBackupSchedule) {
  schedule = { ...schedule };
  await withOperation(async () => {
    invalidateSchedulesRead();
    const draft = scheduleDraft;
    const approved = await confirm({
      title: t("panel.backup.schedule.runTitle"),
      message: t("panel.backup.schedule.runMessage"),
      tone: "neutral",
      confirmLabel: t("panel.backup.schedule.runNow"),
      impact: [schedule.userName || schedule.userId]
    });
    if (!approved || !canWrite()) return;

    const dto = await runBackupScheduleNow(schedule.userId);
    if (!isActive()) return;
    await readSchedules();
    if (!isActive()) return;
    await refresh();
    if (!isActive()) return;
    if (draft === scheduleDraft && scheduleForm.value?.userId === dto.userId) {
      applyScheduleToForm(dto);
    }

    // "Başlatıldı" demek yetmez: tur bittiğinde sonucu kaydın kendisi taşır.
    if (dto.lastStatus === "failed" || dto.lastStatus === "completed_with_errors") {
      reportScheduleResult([lastStatusLabel(dto), dto.lastDetails].filter(Boolean).join("\n"), true, true);
      return;
    }

    const confirmed = dto.lastStatus === "completed";
    reportScheduleResult([t(confirmed ? "backup.schedule_run" : "panel.backup.schedule.resultUnconfirmed"), dto.lastDetails].filter(Boolean).join("\n"), !confirmed, true);
  });
}

function retentionLabel(schedule: SubscriptionBackupSchedule) {
  const parts: string[] = [];
  parts.push(schedule.retentionDays > 0
    ? t("panel.backup.schedule.retentionValue", { days: schedule.retentionDays })
    : t("panel.backup.schedule.unlimited"));
  if (schedule.keepCount > 0) {
    parts.push(t("panel.backup.schedule.keepValue", { count: schedule.keepCount }));
  }
  return parts.join(" · ");
}

function targetLabel(targetType: string) {
  const key = `panel.backup.target.${targetType}`;
  const translated = t(key);
  return translated === key ? targetType : translated;
}

/**
 * Bir "son çalışma" durumunun okunur karşılığı — TEK KAYNAK.
 *
 * Abonelik programı (`lastStatus`) ve sunucu geneli otomatik yedek (`lastBackupStatus`) AYNI
 * sözlüğü kullanıyor ("completed" | "completed_with_errors" | "failed"), bu yüzden iki ekran
 * için iki ayrı eşleme yazılmaz.
 */
function lastStatusText(status: string) {
  if (!status) {
    return t("panel.backup.schedule.never");
  }

  const key = `panel.backup.schedule.lastStatus.${status}`;
  const translated = t(key);
  return translated === key ? status : translated;
}

function lastStatusLabel(schedule: SubscriptionBackupSchedule) {
  return lastStatusText(schedule.lastStatus);
}

function lastStatusTone(status: string) {
  if (status === "failed") return "danger";
  if (status === "completed_with_errors") return "warn";
  if (status === "completed") return "ok";
  return "";
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(value: string | null) {
  return formatDateTime(value, "—");
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

function formatStatus(status: string) {
  const key = `panel.backup.status.${status}`;
  const translated = t(key);
  return translated === key ? status : translated;
}

/** Eksik öğe taşıyan durumlar YEŞİL gösterilmez; "tamam" rozeti gerçeği örterdi. */
function statusTone(status: string) {
  if (status === "created_with_errors" || status === "restored_with_errors" || status === "created_remote_failed") {
    return "warn";
  }
  return "ok";
}
</script>

<style scoped>
.backup-panel,
.backup-create {
  align-content: start;
}

.backup-table {
  --backup-row-min-width: 1040px;
}

.backup-create {
  grid-template-columns: minmax(0, 1fr);
}

.backup-create .section-head,
.backup-create .section-head > * {
  min-width: 0;
  max-width: 100%;
}

/* Eylem sütunu SABİT genişlikte: düğmeler her satırda aynı düzende otursun, satırdan satıra
   yeri kaymasın (oransal genişlikte uzun yedek adları düğme sütununu daraltıp diziliş bozuyordu). */
.backup-row {
  min-width: var(--backup-row-min-width);
  grid-template-columns: minmax(200px, 1.3fr) minmax(160px, 1.2fr) minmax(110px, 0.9fr) minmax(150px, 1fr) 320px;
  align-items: start;
}

.backup-row .row-actions {
  align-content: start;
  gap: var(--sp-6);
}

.backup-row .retry-unavailable {
  flex: 1 1 100%;
}

/* Güvenli eylemler TAM GENİŞLİK bir kutuda: kendi içinde sarar, ardından gelen "Sil"i zorunlu
   olarak bir alt satıra iter. Sonuç: yıkıcı düğmenin yeri kayıttan kayda DEĞİŞMEZ. */
.backup-row .safe-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-6);
  flex: 1 1 100%;
}

/* "Sil" kendi satırında SAĞA yaslanır: güvenli düğmelerin dizisiyle aynı x'te başlamaz, yani
   sarma sırasında parmağın/farenin alışkanlık kazandığı yere denk gelmez. */
.backup-row .row-actions > .danger {
  margin-left: auto;
}

.schedule-row {
  min-width: 1240px;
  grid-template-columns:
    minmax(180px, 1.1fr) minmax(110px, 0.8fr) minmax(145px, 0.9fr)
    minmax(130px, 0.9fr) minmax(110px, 0.7fr) minmax(210px, 1.2fr) 250px;
  align-items: start;
}

.schedule-row .row-actions {
  align-content: start;
  gap: var(--sp-6);
}

.last-details {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.quota-warn {
  color: var(--warn-text);
}

.entry-row {
  grid-template-columns: 24px minmax(0, 1fr) 80px;
  min-width: 0;
  align-items: center;
}

.entry-row input[type="checkbox"] {
  width: auto;
}

.entry-group-head {
  display: flex;
  justify-content: space-between;
  gap: var(--sp-8);
  padding: var(--sp-8) var(--sp-4) var(--sp-2);
  border-top: 1px solid var(--panel-border);
}

/* ===================== GÜN GRUPLARI ===================== */

/* Gün başlığı, kaydırılabilir tablonun İÇİNDE durur ve satırlarla aynı en az genişliği alır:
   aksi hâlde tablo yana kaydırıldığında başlık yerinde kalıp satırlardan kopuk görünüyordu. */
.day-group-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-10);
  flex-wrap: wrap;
  min-width: var(--backup-row-min-width);
  /* 9px 11px -> --sp-10 --sp-12: ölçeğe girmenin YANINDA ölçülebilir bir hizalama da getiriyor.
     `.data-row` dolgusu tam olarak 10/12; gün başlığı 9/11 ile her kenarda 1px içeride
     duruyordu, yani başlığın metni altındaki satırların metniyle AYNI x'te başlamıyordu. */
  padding: var(--sp-10) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-2);
}

/* Başlığın TAMAMI tıklanır: küçük bir oka isabet ettirmek dokunmatik ekranda zordu. */
.day-toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-8);
  width: auto;
  padding: var(--sp-2) 0;
  border: none;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.day-caret {
  width: 1em;
  color: var(--text-muted);
}

.day-summary {
  align-items: center;
}

/* ===================== YEDEĞİN YERİ ===================== */

.location-toggle {
  width: auto;
  justify-self: start;
}

.location-detail {
  display: grid;
  gap: var(--sp-8);
  padding: var(--sp-10) var(--sp-12) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
}

.location-line {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

.location-line button,
.location-root button {
  width: auto;
}

/* Tam yol KISALTILMAZ (kullanıcı kopyalayıp yapıştıracak) ama satırı da bozmaz: kutu KENDİ
   içinde yatay kayar. `nowrap` bilinçli — sarılmış bir yol yanlış okunuyor. */
.path-box {
  flex: 1 1 260px;
  min-width: 0;
  max-width: 100%;
  overflow-x: auto;
  padding: var(--sp-4) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  /* Kendi yedek listesini yazmak yerine `--font-mono`: base.css'teki belirtecin var olma sebebi
     tam olarak buydu (aynı panelde iki farklı konsol yazı tipi çiziliyordu). 0.8rem da ölçek
     dışıydı; --fs-2xs (0.78rem) ile fark 0.27px, yani görünüm aynı, karar noktası bir eksik. */
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  white-space: nowrap;
}

/* Tablonun DIŞINDA, üstünde duran bağlam şeridi. `border-top` KALDIRILDI: tablonun içindeyken
   satırları birbirinden ayırmak için vardı; artık ayrı bir öge olduğu için kendi hafif
   çerçevesini alıyor (panelin `.module-toolbar` diliyle aynı). */
.location-root {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
  margin: 0;
  padding: var(--sp-8) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-0);
}

.toolbar-search {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-6);
  flex: 1 1 220px;
  min-width: 0;
}

.toolbar-search input {
  min-width: 0;
}

.toolbar-search > .meta {
  flex-shrink: 0;
  white-space: nowrap;
}

.auto-run-line {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

.entry-link {
  justify-self: start;
  text-align: left;
  min-width: 0;
  max-width: 100%;
  white-space: normal;
  overflow-wrap: anywhere;
  display: inline-flex;
  gap: var(--sp-6);
}

.entry-file {
  overflow-wrap: anywhere;
}

.breadcrumb {
  display: flex;
  gap: var(--sp-4);
  flex-wrap: wrap;
}

.breadcrumb button {
  width: auto;
  max-width: 100%;
  overflow-wrap: anywhere;
  white-space: normal;
}

.breadcrumb [aria-current="location"] {
  border-color: var(--accent-1);
  color: var(--text-strong);
}

.archive-name {
  margin: var(--sp-4) 0;
  color: var(--text-soft);
  overflow-wrap: anywhere;
  font-family: var(--font-mono);
  font-size: var(--fs-xs);
}

.archive-selection,
.archive-destination {
  display: grid;
  gap: var(--sp-8);
  padding: var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-0);
  min-width: 0;
}

.archive-selection h4 {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  margin: 0;
}

.archive-destination summary { cursor: pointer; font-weight: 600; }
.archive-destination[open] > :not(summary) { margin-top: var(--sp-10); }

.selected-paths {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--sp-6);
  max-height: 220px;
  overflow-y: auto;
}

.selected-paths li {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  min-width: 0;
}

.selected-paths code {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
  font-size: var(--fs-xs);
}

.selected-paths button {
  flex: 0 0 auto;
  width: 36px;
  min-height: 36px;
}

@media (max-width: 520px) {
  .archive-entries .entry-row {
    grid-template-columns: 22px minmax(0, 1fr) 58px;
    gap: var(--sp-6);
    padding-inline: var(--sp-6);
  }

  .archive-entries .cell-muted {
    font-size: var(--fs-2xs);
    text-align: right;
  }
}

.backup-browser,
.chain-panel,
.schedules-panel {
  align-content: start;
  display: grid;
  gap: var(--sp-12);
}

.row-actions button {
  width: auto;
}

.remote-url {
  grid-column: span 1;
}

.encryption-card {
  align-content: start;
  display: grid;
  gap: var(--sp-12);
}

/* Parola kaybı geri dönüşsüz: uyarı satırı gövde metninden ayrışsın. */
.encryption-loss {
  font-weight: 600;
}

/* Durum + kilit + uyarı rozetleri aynı satırda, dar ekranda alt satıra sarar. */
.pill-row {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-4);
}

/* Rozet aynı zamanda düğme: dokunmatik ekranda `title` ipucu hiç görünmüyordu. */
.warning-toggle {
  width: auto;
  cursor: pointer;
  border: none;
}

/* AYRINTI KUTULARI YUKARIDAKI SATIRA BAGLANIR.
   Kutu, satirin kapanisindan SONRA kardes oge olarak ciziliyor; yani iki satirin ARASINDA
   duruyor ve hicbir gorsel ipucu yokken ALTTAKININ basligi gibi okunuyordu (kullanici bildirdi).
   Sol kenardaki renkli cubuk + girinti, kutuyu YUKARIDAKINE baglar; ust kenarlik BILEREK yok
   (kenarlik ayirir, biz birlestirmek istiyoruz). Ad zaten baslikta yaziyor -- bu stil onun
   yerine gecmez, YANINDA durur. */
.warning-detail,
.location-detail {
  position: sticky;
  left: var(--sp-16);
  margin-left: var(--sp-16);
  border-left: 3px solid var(--warn-1);
  border-top: none;
  background: var(--surface-2);
  /* Yatay kaydırılan satırın açıklaması görünür tablo alanında okunabilir kalır. */
  grid-template-columns: minmax(0, 1fr);
  min-width: 0;
  overflow-wrap: anywhere;
}

.location-detail {
  border-left-color: var(--accent-1);
}

/* AYRINTISI AÇIK SATIR ile KUTUSU TEK BLOK.
   Aynı zemin + aradaki kenarlığın kaldırılması, "kutu bu satıra ait" bilgisini metinden bağımsız
   olarak da verir. `:hover` kuralı base.css'te (0,2,0); buradaki kural kapsamlı olduğu için
   [data-v] ile (0,3,0) — üzerine gelindiğinde işaret KAYBOLMAZ. */
.data-row.backup-row.row-expanded,
.data-row.backup-row.row-expanded:hover {
  background: var(--surface-2);
  border-bottom-color: transparent;
}

.detail-title {
  display: block;
  margin-bottom: var(--sp-6);
}

.warning-detail {
  padding: var(--sp-8) var(--sp-12) var(--sp-12);
  display: grid;
  gap: var(--sp-4);
  /* Kutunun ALT kenarı gerekli: satırın alt kenarlığı artık (row-expanded) saydam, yani blok
     yukarıdan açıldı. Alt çizgi olmasaydı ayrıntı metni bir SONRAKİ yedeğin satırına akmış
     görünürdü — düzeltilen hatanın tam olarak aynası. `.location-detail`te zaten vardı. */
  border-bottom: 1px solid var(--panel-border);
}

.warning-detail ul,
.warning-lines {
  margin: 0;
  padding-left: var(--sp-16);
  display: grid;
  gap: var(--sp-4);
}

/* `.notice` gövdesi `pre-wrap`: işaretlemedeki girintiler METİN olarak çizilirdi. Liste
   biçimlendirmesi burada devralınır, uyarı metinlerinin kendi satır sonları korunur. */
.encryption-card .notice.encryption-loss {
  white-space: normal;
}

.warning-lines li {
  white-space: pre-wrap;
}

.warning-detail small {
  display: block;
  opacity: 0.75;
  overflow-wrap: anywhere;
}

/* Zincir dikey bir zaman çizgisi: halkaların SIRASI geri yükleme sırasıdır. */
.chain-links {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--sp-6);
}

.chain-item {
  display: grid;
  grid-template-columns: 130px minmax(160px, 1fr) 170px 90px 110px;
  gap: var(--sp-8);
  align-items: center;
  padding: var(--sp-6) var(--sp-8);
  border-left: 3px solid var(--panel-border);
}

.chain-item.current {
  border-left-color: var(--accent-1);
  font-weight: 600;
}

/* Dosyası olmayan halka: üstü çizili + kırmızı. Zincirin nerede koptuğu tek bakışta görünmeli. */
.chain-item.missing .chain-name {
  text-decoration: line-through;
  color: var(--danger-text);
}

.chain-name {
  overflow-wrap: anywhere;
  display: grid;
}

.scope-grid {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-12);
}

.schedule-form {
  display: grid;
  gap: var(--sp-12);
}

.schedule-add {
  align-items: end;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }

  .chain-item {
    grid-template-columns: 1fr;
  }
}
</style>
