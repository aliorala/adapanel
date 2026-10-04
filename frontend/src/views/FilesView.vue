<template>
  <section class="panel module-shell files-panel">
    <div class="module-head">
      <div>
        <h2 v-if="props.embedded">{{ t("panel.files.title") }}</h2>
        <p class="meta">{{ t("panel.files.subtitle") }}</p>
      </div>
      <button class="secondary compact" @click="() => refresh()">
        {{ t("panel.files.refresh") }}
      </button>
    </div>

    <!-- BİLDİRİM ŞERİDİ. Yeri ÖLÇÜLDÜ, tahmin edilmedi: `page-notice` base.css'te
         `position: fixed` ile sağ alt köşeye çakılıdır (bkz. base.css `.page-notice`), yani
         kullanıcı sayfanın neresine kaydırırsa kaydırsın mesajı görür. Bu yüzden mesajı ayrıca
         her eylemin yanına kopyalamıyoruz — ikinci bir bildirim yeri, iki farklı doğruluk demek.
         Eksik olan tek şey KAPATMA idi: şerit bir sonraki bildirime kadar ekranda kalıyor ve
         çok satırlı bir yükleme özeti köşeyi kalıcı olarak kaplıyordu. -->
    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <span class="notice-text">{{ notice }}</span>
      <button
        type="button"
        class="notice-dismiss"
        :aria-label="t('panel.common.dismissNotice')"
        :title="t('panel.common.dismissNotice')"
        @click="clearNotice">×</button>
    </p>

    <BackupRestoreResult v-if="fullRestore.result.value" :result="fullRestore.result.value" :refresh-error="fullRestore.refreshError.value" />

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'files'" :class="{ active: activeTab === 'files' }" @click="activeTab = 'files'">
          {{ t("panel.files.tab.files") }}
        </button>
        <button role="tab" :aria-selected="activeTab === 'backups'" :class="{ active: activeTab === 'backups' }" @click="activeTab = 'backups'">
          {{ t("panel.files.tab.backups") }}
        </button>
      </div>
      <div class="site-picker">
        <!-- Genel dosya sayfasında seçici kalır; site çalışma alanı kendi geri yolunu sunar. -->
        <template v-if="!props.embedded">
          <label for="files-site-picker">{{ t("panel.files.siteLabel") }}</label>
          <select id="files-site-picker" :value="selectedSiteName" @change="changeSite">
            <option value="">{{ t("panel.files.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </template>
        <span v-if="workspace?.isDirectory && !loading && !workspaceLoad.message.value" class="status-pill">{{ workspace.entries.length }} {{ t("panel.files.items") }}</span>
      </div>
    </div>

    <section v-if="activeTab === 'files' && !selectedSiteName" class="form-surface empty-state">
      <strong>{{ t("panel.files.siteRequired") }}</strong>
      <p class="meta">{{ t("panel.files.siteRequiredHint") }}</p>
    </section>

    <!-- SÜRÜKLE-BIRAK bütün çalışma kartını kaplar: kullanıcı dosyayı "listenin üstüne" bırakır,
         ama fare tabloya girmeden önce de bir hedef görmeli. `dragover`da `preventDefault`
         ZORUNLU — yoksa tarayıcı bırakmayı hiç kabul etmez ve dosyayı sekmede açar. -->
    <section
      v-else-if="activeTab === 'files'"
      class="form-surface workspace-card"
      :class="{ 'drop-active': dropActive }"
      @dragenter="onDragEnter"
      @dragover="onDragOver"
      @dragleave="onDragLeave"
      @drop="onDrop">
      <div v-if="dropActive" class="drop-overlay" role="status" aria-live="polite">
        <strong>{{ t("panel.files.dropHere") }}</strong>
        <small>{{ t("panel.files.dropHint") }}</small>
      </div>

      <!-- ARAÇ ÇUBUĞU İKİ İŞE AYRILDI: solda GEZİNME, sağda OLUŞTURMA.
           Önceden dördü ("Dosya yükle", "Üst dizin", "Klasör oluştur", "Yeni dosya") tek grupta
           ve HEPSİ `secondary compact` idi: aynı ağırlıkta dört düğmenin arasında birincil eylem
           yoktu ve "Üst dizin" — ki bir gezinme eylemidir, yol çubuğunun işini yapar — oluşturma
           düğmelerinin arasında duruyordu. Şimdi "Üst dizin" ait olduğu yerde, yol çubuğunun
           yanında; "Dosya yükle" ise tek BİRİNCİL eylem olarak vurgulu.
           Dıştaki kapsayıcı `space-between` uyguluyordu, içteki grup da AYNI sınıfı taşıdığı için
           dört düğme boş alana yayılıyordu; iç grup artık `toolbar-group` (yalnız akış + boşluk). -->
      <div class="action-row">
        <div class="toolbar-group">
          <button
            class="secondary compact"
            :disabled="!canGoUp"
            @click="openPath(workspace?.parentPath || siteRootPath)">
            {{ t("panel.files.up") }}
          </button>
          <!-- YOL ÇUBUĞU: her parça tıklanabilir. Site kökünün ÜSTÜ hiç çizilmez — kullanıcının
               sanal alanı orada başlar ve gezinilemeyecek bir parçayı göstermek, olmayan bir
               yetkiyi ima etmek olurdu. Bulunulan parça düğme değil: kendine gitmek işlevsiz. -->
          <nav class="breadcrumb" :aria-label="t('panel.files.breadcrumb')">
            <template v-for="(part, index) in breadcrumb" :key="part.path">
              <span v-if="index > 0" class="breadcrumb-sep" aria-hidden="true">/</span>
              <strong v-if="index === breadcrumb.length - 1" aria-current="page">{{ part.name }}</strong>
              <button v-else type="button" class="link-button" @click="openPath(part.path)">{{ part.name }}</button>
            </template>
          </nav>
        </div>
        <div v-if="workspace?.isDirectory" class="toolbar-group">
          <!-- ÇOKLU seçim: yirmi dosyayı tek tek seçtirmek Plesk'te de yok. Gönderim SIRAYLA
               (bkz. uploadSelectedFiles). -->
          <label class="upload-button" :class="{ disabled: fileMutationBusy || locked || loading || Boolean(workspaceLoad.message.value) || !workspace?.isDirectory }" :title="lockReason || undefined">
            <input type="file" multiple v-bind="writeGate(fileMutationBusy || loading || Boolean(workspaceLoad.message.value) || !workspace?.isDirectory)" @change="uploadSelectedFiles" />
            <span>{{ uploadBusy ? uploadProgressLabel : t("panel.files.upload") }}</span>
          </label>
          <!-- Oluşturma düğmeleri yalnız KLASÖR görünümünde anlamlı: bir dosya açıkken
               `workspace.path` o dosyanın yoludur ve altına klasör/dosya yaratmak sessizce
               yanlış yere yazmak olurdu. -->
          <button class="secondary compact" v-bind="writeGate(fileMutationBusy || loading || Boolean(workspaceLoad.message.value) || !workspace?.isDirectory)" @click="createFolder">
            {{ t("panel.files.createFolder") }}
          </button>
          <button class="secondary compact" v-bind="writeGate(fileMutationBusy || loading || Boolean(workspaceLoad.message.value) || !workspace?.isDirectory)" @click="createNewFile">
            {{ t("panel.files.createFile") }}
          </button>
        </div>
      </div>

      <section v-if="archiveOperation && archiveOperation.status !== 'confirming'"
        class="archive-state" :class="{ 'is-error': Boolean(archiveOperation.error), 'is-success': archiveOperation.status === 'succeeded' }"
        :role="archiveOperation.error ? 'alert' : 'status'"
        :aria-busy="archiveOperation.status === 'running' || archiveOperation.status === 'refreshing'">
        <div class="archive-heading">
          <svg class="archive-symbol" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
            <template v-if="archiveOperation.error"><path d="m12 3 10 18H2L12 3Z" /><path d="M12 9v5m0 3h.01" /></template>
            <template v-else-if="archiveOperation.status === 'succeeded'"><circle cx="12" cy="12" r="9" /><path d="m8 12 2.5 2.5L16 9" /></template>
            <template v-else><rect x="4" y="3" width="16" height="18" rx="2" /><path d="M11 3v3h2v3h-2v3h2m-2 4h2v2h-2z" /></template>
          </svg>
          <h3 v-if="archiveOperation.status === 'refreshing'">{{ t('panel.files.archiveRefreshing') }}</h3>
          <h3 v-else-if="archiveOperation.status === 'failed'">{{ t('panel.files.archiveFailed') }}</h3>
          <h3 v-else-if="archiveOperation.status === 'succeeded'">{{ t(archiveOperation.kind === 'extract' ? 'panel.files.archiveExtractSucceeded' : 'panel.files.archiveCompressSucceeded') }}</h3>
          <h3 v-else>{{ t(archiveOperation.kind === 'extract' ? 'panel.files.archiveExtractRunning' : 'panel.files.archiveCompressRunning') }}</h3>
        </div>
        <dl class="archive-scope">
          <div><dt>{{ t('panel.files.archiveSource') }}</dt><dd>{{ archiveOperation.sourcePath }}</dd></div>
          <div><dt>{{ t('panel.files.archiveDestination') }}</dt><dd>{{ archiveOperation.destinationPath }}</dd></div>
        </dl>
        <p v-if="archiveOperation.error" class="archive-error">{{ archiveOperation.error }}</p>
        <p v-if="archiveOperation.error" class="meta">{{ t('panel.files.archiveFailedHint') }}</p>
        <p v-else-if="archiveOperation.status === 'running'" class="meta">{{ t('panel.files.archivePendingHint') }}</p>
        <p v-if="archiveOperation.refreshError" class="meta">{{ t('panel.files.archiveRefreshFailed') }}</p>
        <div v-if="archiveOperation.status === 'succeeded' && archiveOperation.kind === 'compress'" class="archive-output">
          <div v-if="archiveOperation.output" class="archive-output-details">
            <span class="meta">{{ t('panel.files.archiveOutput') }}</span>
            <strong>{{ archiveOperation.output.name }}</strong>
            <span class="meta">{{ boyutMetni(archiveOperation.output.size) }}</span>
          </div>
          <p v-else class="meta">{{ t('panel.files.archiveOutputUnavailable') }}</p>
          <a v-if="archiveDownloadUrl" class="secondary compact action-link" :href="archiveDownloadUrl">
            <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12m-4-4 4 4 4-4M5 16v4h14v-4" /></svg>
            {{ t('panel.files.archiveDownload') }}
          </a>
        </div>
        <div v-if="archiveOperation.status === 'failed' || archiveOperation.status === 'succeeded'" class="archive-actions">
          <button v-if="archiveOperation.status === 'failed' || (archiveOperation.kind === 'compress' && !archiveDownloadUrl)" type="button" class="secondary compact" :disabled="fileMutationBusy || loading" @click="refreshArchiveListing">{{ t('panel.files.archiveRefresh') }}</button>
          <button type="button" class="link-button" @click="dismissArchiveOperation">{{ t('panel.files.archiveClose') }}</button>
        </div>
      </section>

      <!-- YÜKLEME İLERLEMESİ. Düğme metni zaten "3/20" diyor ama bir SAYI, kalan işin ne kadar
           olduğunu bir bakışta anlatmaz; çubuk oranı gösterir. Ölçü DOSYA BAŞINADIR (uç bayt
           ilerlemesi bildirmiyor), bu yüzden büyük tek dosyada çubuk uzun süre yerinde durur —
           bunu bayt ilerlemesi gibi göstermek uydurma olurdu. -->
      <div
        v-if="uploadBusy"
        class="upload-progress"
        role="progressbar"
        :aria-label="uploadProgressLabel"
        :aria-valuenow="uploadDone"
        aria-valuemin="0"
        :aria-valuemax="uploadTotal">
        <span class="progress-fill" :style="{ width: `${uploadPercent}%` }"></span>
      </div>

      <section v-if="workspace?.isDirectory && !loading && !workspaceLoad.message.value" class="file-search">
        <form class="search-row" :aria-busy="searchBusy" @submit.prevent="runSearch">
          <label class="search-field" for="files-search-query">
            <span>{{ t("panel.files.searchHint") }}</span>
            <input id="files-search-query" v-model="searchQuery" required minlength="2" :disabled="searchBusy" aria-describedby="files-search-scope" autocomplete="off" />
          </label>
          <button type="submit" class="secondary compact" :disabled="searchBusy || searchQuery.trim().length < 2">
            {{ searchBusy ? t("panel.files.searchWorking") : t("panel.files.search") }}
          </button>
          <button v-if="searchRan || searchBusy || searchError" type="button" class="secondary compact" @click="clearSearch">
            {{ t("panel.files.searchClear") }}
          </button>
        </form>
        <small id="files-search-scope" class="meta search-context">{{ t("panel.files.searchScope", { path: workspace.path }) }} {{ t("panel.files.searchMinimum") }}</small>
      </section>

      <section v-if="searchRan || searchBusy || searchError" class="search-panel" :aria-label="t('panel.files.searchResultsTitle')">
        <div class="search-heading">
          <h3>{{ t("panel.files.searchResultsTitle") }}</h3>
          <span v-if="searchRan && !searchBusy && !searchError" class="status-pill">{{ t("panel.files.searchResultCount", { count: searchResults.length }) }}</span>
        </div>
        <p v-if="searchSubmittedQuery" class="meta search-context">{{ t("panel.files.searchResultsFor", { query: searchSubmittedQuery }) }}</p>
        <p v-if="searchBusy" class="meta" role="status" aria-live="polite">{{ t("panel.files.searchWorking") }}</p>
        <SectionLoadError v-else-if="searchError" :message="searchError" @retry="runSearch" />
        <template v-else-if="searchRan">
          <p v-if="searchScannedFiles !== null" class="meta">{{ t("panel.files.searchScanned", { count: searchScannedFiles }) }}</p>
          <p v-if="searchTruncated" class="search-warning" role="status">{{ hataMetni(searchTruncatedReason) }}</p>
          <div v-if="searchResults.length" class="search-results">
          <button
            v-for="entry in searchPager.items.value"
            :key="entry.path"
            class="search-result"
            @click="openPath(entry.path)">
            <strong>{{ entry.name }}</strong>
            <small>{{ entry.path }}</small>
          </button>
          <TablePager :state="searchPager" />
          </div>
          <p v-else class="empty-state">{{ t("panel.files.searchEmpty") }}</p>
        </template>
      </section>

      <!-- "Yükleniyor", "klasör boş" ve "okunamadı" AYRI durumlar: üçü de aynı boş tabloya
           düşüyordu ve kullanıcı listenin gelmediğini mi yoksa klasörün gerçekten boş mu
           olduğunu ayırt edemiyordu. -->
      <p v-if="loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

      <SectionLoadError v-else-if="workspaceLoad.message.value" :message="workspaceLoad.message.value" @retry="() => refresh()" />

      <template v-else-if="workspace?.isDirectory">
        <h3 v-if="searchRan || searchBusy || searchError" class="folder-list-heading">{{ t("panel.files.searchFolderList") }}</h3>
        <!-- TOPLU EYLEM ÇUBUĞU. "Seçilenleri sil" YIKICI eylemdir ve önceden "Kopyala" ile
             "Seçimi temizle" arasında, tam da refleksle basılan yerde duruyordu. Artık kendi
             grubunda, ayırıcı bir çizginin ardında ve çubuğun öbür ucunda: yanlış düğmeye
             çarpmak için farenin gitmesi gereken yol bilinçli olarak uzatıldı. Onay diyaloğu
             (ad/sayı yazdırma) yerinde duruyor; bu yerleşim onun yerine geçmez, önüne geçer. -->
        <div v-if="selection.count.value > 0" class="bulk-bar" role="status">
          <span class="status-pill">{{ t("panel.bulk.selectedCount", { count: selection.count.value }) }}</span>
          <button class="secondary compact" v-bind="writeGate(fileMutationBusy)" @click="startTransfer('move')">
            {{ t("panel.files.move") }}
          </button>
          <button class="secondary compact" v-bind="writeGate(fileMutationBusy)" @click="startTransfer('copy')">
            {{ t("panel.files.copy") }}
          </button>
          <button class="secondary compact" :disabled="fileMutationBusy" @click="selection.clear">
            {{ t("panel.bulk.clearSelection") }}
          </button>
          <span class="bulk-spacer" aria-hidden="true"></span>
          <button class="danger compact" v-bind="writeGate(fileMutationBusy)" @click="removeSelected">
            {{ t("panel.bulk.deleteSelected", { count: selection.count.value }) }}
          </button>
        </div>

        <!-- HEDEF KLASÖR SORULUR, TAHMİN EDİLMEZ. Yol elle yazılabilir (site kökünün altındaki
             her klasör hedef olabilir) ama listeden seçmek yazım hatasını tümüyle ortadan
             kaldırır: yanlış yazılmış bir hedef, arka uçta "hedef klasör yok" ile reddedilir. -->
        <form v-if="transferMode && selection.count.value > 0" class="transfer-form" @submit.prevent="runTransfer">
          <label>
            <span>{{ t("panel.files.destination") }}</span>
            <input v-model="transferTarget" :disabled="fileMutationBusy" :placeholder="siteRootPath" />
            <small class="field-hint">{{ t("panel.files.destinationHint") }}</small>
          </label>
          <label>
            <span>{{ t("panel.files.destinationPick") }}</span>
            <select v-model="transferTarget" :disabled="fileMutationBusy">
              <option :value="siteRootPath">{{ t("panel.files.destinationRoot") }}</option>
              <option v-if="parentTargetPath" :value="parentTargetPath">{{ t("panel.files.destinationParent") }}</option>
              <option v-for="folder in folderTargets" :key="folder.path" :value="folder.path">{{ folder.name }}</option>
            </select>
          </label>
          <label class="overwrite-field">
            <input type="checkbox" v-model="transferOverwrite" :disabled="fileMutationBusy" />
            <span>{{ t("panel.files.overwrite") }}</span>
          </label>
          <div class="entry-actions">
            <button type="submit" v-bind="writeGate(fileMutationBusy || !transferTarget)">
              {{ transferMode === "move" ? t("panel.files.move") : t("panel.files.copy") }}
            </button>
            <button type="button" class="secondary compact" :disabled="fileMutationBusy" @click="transferMode = null">
              {{ t("panel.common.cancel") }}
            </button>
          </div>
        </form>

        <!-- TABLO ANLAMI. Yapı `<table>` değil, ızgara `<div>`lerdir (panelin bütün listeleri
             böyle; `.data-table`/`.data-row` base.css'te tanımlı). Bunun bedeli, ekran
             okuyucunun burada HİÇBİR tablo görmemesiydi: başlık hücresi ile veri hücresi
             arasında bağ yok, "Boyut sütunu" diye bir şey duyulmuyordu. Yapıyı `<table>`ya
             çevirmek ızgara sütunlarını ve base.css'in bütün liste stilini kırardı; ARIA
             rolleri aynı bağı yapıya dokunmadan kurar (`columnheader` = `<th scope="col">`
             karşılığı). -->
        <div class="data-table files-table" role="table" :aria-label="t('panel.files.title')">
          <div class="data-row table-head files-row" role="row">
            <span class="select-cell" role="columnheader">
              <input
                type="checkbox"
                :checked="selection.allSelected(visiblePaths)"
                :disabled="fileMutationBusy"
                :aria-label="t('panel.bulk.selectAll')"
                @change="selection.toggleAll(visiblePaths)" />
              <span class="mobile-select-label" aria-hidden="true">{{ t("panel.bulk.selectAll") }}</span>
            </span>
            <span role="columnheader">{{ t("panel.files.name") }}</span>
            <span role="columnheader">{{ t("panel.files.kind") }}</span>
            <span role="columnheader" class="size-cell">{{ t("panel.files.sizeColumn") }}</span>
            <span role="columnheader" class="date-cell">{{ t("panel.files.modified") }}</span>
            <span role="columnheader">{{ t("panel.files.actions") }}</span>
          </div>

          <template v-for="entry in filePager.items.value" :key="entry.path">
            <div class="data-row files-row" role="row">
              <span class="select-cell" role="cell">
                <input
                  type="checkbox"
                  :checked="selection.isSelected(entry.path)"
                  :disabled="fileMutationBusy"
                  :aria-label="t('panel.bulk.selectRow', { name: entry.name })"
                  @change="selection.toggle(entry.path)" />
              </span>
              <!-- TAM YOL ARTIK HER SATIRDA YAZILMIYOR. Bir klasörün bütün ögeleri aynı üst yolu
                   paylaşır: 25 satır boyunca "sites/x/wp-content/plugins/" tekrarlanıyor, satırı
                   iki katına çıkarıyor ve hiçbir şey ayırt etmiyordu — üstelik aynı bilgi zaten
                   yol çubuğunda duruyor. Bilgi KAYBOLMADI: ada asılı `title` ile okunabilir, ve
                   yolun gerçekten satırdan satıra değiştiği tek yerde (arama sonuçları) hâlâ
                   yazılı. -->
              <div class="cell-main" role="cell">
                <button class="link-button entry-name" :title="entry.path" @click="openPath(entry.path)">
                  {{ entry.name }}
                </button>
              </div>
              <span class="kind-cell" role="cell" :data-label="t('panel.files.kind')">{{ entry.isDirectory ? t("panel.files.folder") : entry.extension || t("panel.files.file") }}</span>
              <!-- KLASÖR BOYUTU İSTENDİĞİNDE hesaplanır. Hesaplanmamış klasör "0 B" göstermez:
                   sıfır ÖLÇÜLMÜŞ bir değerdir ve dolu bir klasörü boş göstermek kullanıcının
                   disk kararını yanlış sayıya dayandırması olurdu.
                   ÖLÇÜM DÜĞMESİ HÜCRENİN İÇİNE ALINDI: eylem, sonucunun görüneceği yerde durur;
                   "—" ise ne olduğu belirsiz bir tire yerine "Boyutu hesapla" diyerek hem
                   ölçülmediğini söyler hem de çaresini gösterir. Bu, işlemler sütunundan bir
                   düğme daha eksiltir. -->
              <span class="size-cell" role="cell" :data-label="t('panel.files.sizeColumn')">
                <template v-if="!entry.isDirectory">{{ boyutMetni(entry.size) }}</template>
                <span v-else-if="folderSizes[entry.path]" class="folder-size-result">
                  <span :title="folderSizes[entry.path].truncated ? kesikSebebi(folderSizes[entry.path]) : undefined">
                    {{ folderSizes[entry.path].truncated
                      ? t("panel.files.sizeAtLeast", { size: boyutMetni(folderSizes[entry.path].totalBytes) })
                      : boyutMetni(folderSizes[entry.path].totalBytes) }}
                  </span>
                  <button v-if="folderSizes[entry.path].truncated" type="button" class="link-button size-explanation" @click="showNotice(kesikSebebi(folderSizes[entry.path]), true)">{{ t("panel.files.partialSizeDetails") }}</button>
                  <button type="button" class="link-button size-calc" :disabled="Boolean(sizeBusyPath)" @click="calculateFolderSize(entry)">
                    {{ sizeBusyPath === entry.path ? t("panel.files.calcSizeBusy") : t("panel.files.recalculateSize") }}
                  </button>
                </span>
                <button
                  v-else
                  type="button"
                  class="link-button size-calc"
                  :disabled="Boolean(sizeBusyPath)"
                  @click="calculateFolderSize(entry)">
                  {{ sizeBusyPath === entry.path ? t("panel.files.calcSizeBusy") : t("panel.files.calcSize") }}
                </button>
              </span>
              <span class="date-cell" role="cell" :data-label="t('panel.files.modified')">{{ tarihMetni(entry.lastModifiedUtc) }}</span>
              <!-- EYLEM HİYERARŞİSİ. Satırda altı düğmeye kadar çıkılıyordu (İndir · Boyutu
                   hesapla · Yeniden adlandır · ZIP yap · Arşivi aç · Sil) ve hepsi aynı
                   ağırlıktaydı; sütun tek başına 430px ayırıyordu (eski CSS). Daha kötüsü,
                   `flex-wrap` yüzünden "Sil" satırdan satıra BAŞKA bir yatay konuma düşüyordu:
                   .zip satırında bir düğme fazla olduğu için, bir üst satırda "ZIP yap"ın
                   bulunduğu noktada "Sil" oluyordu.
                   Şimdi görünür kalan tek eylem, dosya yöneticisinde en sık kullanılan "İndir";
                   gerisi tek bir açma düğmesinin ardında ve KONUMU SABİT. -->
              <div class="entry-actions" role="cell">
                <a v-if="!entry.isDirectory" class="secondary compact action-link" :href="getDownloadUrl(entry.path)">
                  {{ t("panel.files.download") }}
                </a>
                <button
                  type="button"
                  class="secondary compact more-toggle"
                  :aria-expanded="openActionsPath === entry.path"
                  :aria-controls="`file-actions-${entry.path}`"
                  :aria-label="t('panel.files.moreActions', { name: entry.name })"
                  :title="t('panel.files.moreActions', { name: entry.name })"
                  @click="toggleActions(entry.path)">…</button>
              </div>
            </div>

            <!-- İKİNCİL EYLEMLER — AÇILIR KATMAN DEĞİL, SATIR ALTI ÇEKMECE.
                 Üste binen bir menü burada ÇALIŞMAZ: `.data-table` base.css'te `overflow-x: auto`
                 taşır ve CSS'te bir eksen `visible` olmayınca öbürü de `auto`ya döner; yani
                 konumlandırılmış bir menü tablonun içinde KIRPILIR ya da tabloya dikey kaydırma
                 çubuğu ekletir. Çekmece satırı bu sorunu tümüyle ortadan kaldırır, dar ekranda ve
                 klavyeyle de aynı biçimde çalışır. -->
            <div
              v-if="openActionsPath === entry.path"
              :id="`file-actions-${entry.path}`"
              class="data-row actions-drawer"
              role="row"
              @keydown.esc.stop="openActionsPath = ''">
              <!-- `aria-colspan`: rol verilmiş bir tabloda satırların hücre sayısı tutmalı;
                   çekmece satırı altı sütunun tamamını kaplar. -->
              <div class="drawer-actions" role="cell" aria-colspan="6">
                <span class="drawer-label">{{ entry.name }}</span>
                <button class="secondary compact" v-bind="writeGate(fileMutationBusy)" @click="renamePath(entry)">{{ t("panel.files.rename") }}</button>
                <button class="secondary compact" v-bind="writeGate(fileMutationBusy)" @click="compress(entry.path)">{{ t("panel.files.compress") }}</button>
                <button v-if="entry.extension.toLowerCase() === '.zip'" class="secondary compact" v-bind="writeGate(fileMutationBusy)" @click="extract(entry.path)">
                  {{ t("panel.files.extract") }}
                </button>
                <!-- Yıkıcı eylem: ayırıcının ardında ve çekmecenin öbür ucunda. -->
                <span class="drawer-divider" aria-hidden="true"></span>
                <button class="danger compact" v-bind="writeGate(fileMutationBusy)" @click="removePath(entry)">{{ t("panel.sites.delete") }}</button>
              </div>
            </div>
          </template>

          <!-- Boş klasör iletisi tablonun İÇİNDE kalır (kutunun dışına atılsa başlık satırı boş
               bir çerçeveyle asılı kalırdı), ama `role="row"`/`role="cell"` ile: rol verilmiş bir
               tablonun içinde rolsüz bir öge ekran okuyucuda yapıyı bozar. -->
          <div v-if="entries.length === 0" class="data-row" role="row">
            <p class="empty-state" role="cell" aria-colspan="6">{{ t("panel.files.emptyFolder") }}</p>
          </div>
        </div>

        <TablePager :state="filePager" :disabled="bulk.busy.value" />
      </template>

      <!-- DÜZENLEYİCİ tek aralıklı yazı tipi kullanır: buradan düzenlenen şeyler `.htaccess`,
           `web.config`, `robots.txt` gibi hizalamanın anlam taşıdığı dosyalar. Orantılı yazı
           tipinde girinti ve sütun hizası okunmuyordu. -->
      <label v-else-if="workspace" class="stacked-field">
        <span class="field-label">{{ t("panel.files.editor") }}</span>
        <span v-if="!canEditFile" id="file-content-state" class="field-hint" role="status">{{ t(workspace.contentUnavailableReasonKey || 'panel.files.contentUnmeasured') }}</span>
        <textarea v-model="fileContent" :disabled="fileMutationBusy || loading || !canEditFile" :aria-describedby="!canEditFile ? 'file-content-state' : undefined" rows="18" spellcheck="false" class="file-editor"></textarea>
      </label>

      <!-- Kaydet BİRİNCİL, indir ikincil. Eskiden ikisi `action-row` içindeydi ve o sınıf
           `space-between` uyguladığı için düğmeler kartın iki ucuna savruluyordu; `toolbar-group`
           yalnız akış + boşluk verir, ikisi yan yana durur. -->
      <div v-if="workspace && !workspace.isDirectory" class="toolbar-group editor-actions">
        <button v-bind="writeGate(fileMutationBusy || loading || !canEditFile || !isDirty)" @click="saveCurrentFile">
          {{ saving ? t("panel.confirm.working") : t("panel.files.save") }}
        </button>
        <span v-if="isDirty" class="status-pill" role="status">{{ t("panel.files.unsaved") }}</span>
        <a class="secondary compact action-link" :href="getDownloadUrl(workspace.path)">
          {{ t("panel.files.download") }}
        </a>
      </div>

      <!-- Yönetim işleminin hedefi ve kapsamı, izin alanlarından önce gösterilir. -->
      <section v-if="workspace && isAdmin && !loading && !workspaceLoad.message.value" class="permissions-section" aria-labelledby="file-permissions-title">
        <header>
          <h3 id="file-permissions-title">{{ t('panel.files.permissionsTitle') }}</h3>
          <p v-if="workspace.path === siteRootPath" class="field-hint">{{ t('panel.files.permissionsRootHint') }}</p>
          <template v-else>
            <p class="permission-target"><span>{{ t('panel.files.permissionsTarget') }}</span><strong>{{ workspace.path }}</strong></p>
            <p id="file-permissions-scope" class="field-hint">{{ t(workspace.isDirectory ? 'panel.files.permissionsFolderHint' : 'panel.files.permissionsFileHint') }}</p>
          </template>
        </header>
      <form v-if="workspace.path !== siteRootPath" class="permissions-row" :aria-busy="permissionsBusy" aria-describedby="file-permissions-scope" @submit.prevent="savePermissions">
        <label>
          <span class="field-label">{{ t("panel.security.principal") }}</span>
          <input v-model="permissionPrincipal" :disabled="fileMutationBusy" required autocomplete="off" spellcheck="false" placeholder="IIS_IUSRS" />
        </label>
        <!-- SEÇENEKLER ANLAMLARIYLA YAZILIYOR. Liste yalnız "R / M / F" gösteriyordu; bunlar
             icacls'in ham kodlarıdır ve seçili değer olarak ekranda tek başına duran "M"
             kullanıcıya HİÇBİR ŞEY anlatmıyordu. Harf KALDIRILMADI (yöneticinin icacls
             çıktısıyla eşleştirebilmesi için parantez içinde duruyor); yanına ne yaptığı
             yazıldı. Gönderilen değer aynı: R / M / F. -->
        <label>
          <span class="field-label">{{ t("panel.security.rights") }}</span>
          <select v-model="permissionRights" :disabled="fileMutationBusy">
            <option value="R">{{ t("panel.security.rights.read") }}</option>
            <option value="M">{{ t("panel.security.rights.modify") }}</option>
            <option value="F">{{ t("panel.security.rights.full") }}</option>
          </select>
        </label>
        <button type="submit" class="secondary compact" v-bind="writeGate(!canSetPermissions || !permissionPrincipal.trim())">{{ permissionsBusy ? t("panel.confirm.working") : t("panel.files.permissions") }}</button>
      </form>
      </section>
    </section>

    <section v-else class="form-surface workspace-card">
      <button v-if="!isAdmin" class="secondary compact" @click="router.push({ query: { section: 'backup' } })">{{ t("panel.backup.manageScoped") }}</button>
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.title") }}</h3>
          <p class="meta">{{ t("panel.backup.subtitle") }}</p>
        </div>
        <button class="secondary compact" @click="refreshBackups">
          {{ t("panel.backup.refresh") }}
        </button>
      </div>

      <div class="form-grid">
        <label>
          <span>{{ t("panel.php.targetSite") }}</span>
          <select v-model="backupSiteName">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>
        <label v-if="isAdmin">
          <span>{{ t("panel.database.engineLabel") }}</span>
          <select v-model="backupEngine">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
            <option value="postgresql">PostgreSQL</option>
          </select>
        </label>
      </div>

      <div class="action-row">
        <button v-bind="writeGate(!backupSiteName)" @click="createSiteBackup">
          {{ t("panel.backup.siteAction") }}
        </button>
        <button v-if="isAdmin" class="secondary compact" v-bind="writeGate()" @click="createDatabaseBackup">
          {{ t("panel.backup.dbAction") }}
        </button>
      </div>

      <div v-if="gorunenYedekler.length" class="data-table" role="table" :aria-label="t('panel.backup.title')">
        <div class="data-row table-head backup-row" role="row">
          <span role="columnheader">{{ t("panel.files.name") }}</span>
          <span role="columnheader">{{ t("panel.backup.type") }}</span>
          <span role="columnheader">{{ t("panel.sites.state") }}</span>
          <!-- Başlık BOŞ bırakılmıyordu diye değil, ekran okuyucu "sütun 4" deyip susuyordu diye:
               görsel olarak boş kalması doğru (düğme sütunu), adı olması da öyle. -->
          <span role="columnheader"><span class="sr-only">{{ t("panel.files.actions") }}</span></span>
        </div>

        <div v-for="item in gorunenYedekler" :key="item.id" class="data-row backup-row" role="row">
          <div class="cell-main" role="cell">
            <strong>{{ item.name }}</strong>
            <small>{{ item.source }}</small>
          </div>
          <span role="cell">{{ t(`panel.backup.kind.${item.type}`) }}</span>
          <span role="cell">{{ t(`panel.backup.status.${item.status}`) }}</span>
          <span class="entry-actions" role="cell">
            <button v-if="isAdmin" class="secondary compact" v-bind="writeGate(Boolean(fullRestore.restoringId.value))" @click="restore(item)">
              {{ t("panel.backup.restore") }}
            </button>
          </span>
        </div>
      </div>

      <!-- "YEDEK YOK" ile "BU SİTENİN YEDEĞİ YOK" AYRI. Liste boşken ekranda hiçbir şey
           çizilmiyordu: kullanıcı listenin gelmediğini mi, hiç yedek olmadığını mı, yoksa
           süzgecin (bu ekran yalnız SEÇİLİ sitenin yedeklerini gösterir) her şeyi elediğini mi
           anlayamıyordu. Üçüncüsü en yanıltıcısıydı, çünkü sunucuda yedek VAR. -->
      <p v-else-if="!backupsLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else class="empty-state">
        {{ backups.length ? t("panel.backup.emptyFiltered") : t("panel.backup.empty") }}
      </p>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { backupDatabase, backupSite, getBackups, type BackupRecord } from "../api/backup";
import { getSessionEpoch } from "../api/client";
import {
  compressPath,
  copyEntries,
  createDirectory,
  createFile,
  deletePath,
  extractPath,
  getDirectorySize,
  getDownloadUrl,
  getFileWorkspace,
  moveEntries,
  renameEntry,
  saveFile,
  searchFiles,
  updatePermissions,
  uploadFile,
  type DirectorySizeResult,
  type FileEntry,
  type FileWorkspace
} from "../api/files";
import { useBackupRestore, restoreResult } from "../composables/useBackupRestore";
import BackupRestoreResult from "../components/BackupRestoreResult.vue";
import { UserFacingError } from "../utils/apiError";
import { useNavigationGuard, allowNavigation } from "../composables/navigationGuard";
import { useAuthStore } from "../stores/auth";
import router from "../router";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import TablePager from "../components/TablePager.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useBulkAction, useBulkSelection } from "../composables/useBulkSelection";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { formatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

const props = defineProps<{ embedded?: boolean }>();
const { t } = useI18n();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
// `locked`/`lockReason` yükleme için ayrıca gerekiyor: yükleme bir <label> içindeki dosya
// seçicidir, `disabled` etiketin görünümünü değiştirmez — sebebi elle söylemek gerekir.
const { writeGate, locked, lockReason } = useSubscriptionGate();
const { prompt } = usePrompt();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const fullRestore = useBackupRestore(() => sitesStore.selectedSiteName);

const activeTab = ref<"files" | "backups">("files");
const workspace = ref<FileWorkspace | null>(null);
// Dosya listesi sunucudan gelene kadar tablo BOŞ görünüyordu: 2000 dosyalı bir klasör "boş
// klasör" sanılabiliyordu. Yükleniyor / boş / okunamadı artık üç ayrı durum.
const loading = ref(false);
const workspaceLoad = useSectionError();
const fileContent = ref("");
const savedContent = ref("");
const saving = ref(false);
const canEditFile = computed(() => Boolean(workspace.value && !workspace.value.isDirectory && workspace.value.contentLoaded === true));
const isDirty = computed(() => canEditFile.value && fileContent.value !== savedContent.value);
let workspaceRequest = 0;
let fileNavigationGeneration = 0;
let requestedWorkspacePath = "";
let disposed = false;
const fileSession = authStore.session;
const fileActor = fileSession?.user?.id;
const fileRole = fileSession?.user?.role;
const fileEpoch = getSessionEpoch();
const isFileViewActive = () => !disposed && fileEpoch === getSessionEpoch() &&
  authStore.session === fileSession && authStore.session?.isAuthenticated === true &&
  authStore.session.user?.id === fileActor && authStore.session.user?.role === fileRole;
const archiveBusy = ref(false);
type ArchiveOperation = {
  kind: "compress" | "extract";
  sourcePath: string;
  destinationPath: string;
  status: "confirming" | "running" | "refreshing" | "succeeded" | "failed";
  error: string;
  refreshError: string;
  output: { path: string; name: string; size: number } | null;
};
const archiveState = ref<ArchiveOperation | null>(null);
let archiveContext: (() => boolean) | null = null;
const archiveOperation = computed(() => archiveState.value && archiveContext?.() ? archiveState.value : null);
const archiveDownloadUrl = computed(() => {
  const operation = archiveOperation.value;
  if (!operation?.output || operation.status !== "succeeded" || fileMutationBusy.value || loading.value || workspaceLoad.message.value) return null;
  return getDownloadUrl(operation.output.path);
});
function filePathKey(path: string) {
  return path.trim().replace(/\\/g, "/").replace(/^\/+|\/+$/g, "").toLowerCase();
}
function archiveOutput(workspace: FileWorkspace, source: string) {
  const expected = filePathKey(`${source.replace(/[\\/]+$/, "")}.zip`);
  const entry = workspace.entries.find(item => !item.isDirectory && filePathKey(item.path) === expected);
  return entry ? { path: entry.path, name: entry.name, size: entry.size } : null;
}
watch(() => [authStore.session, authStore.session?.user?.id, authStore.session?.user?.role], () => {
  if (!isFileViewActive()) archiveState.value = null;
}, { flush: "sync" });
let searchRequest = 0;
function currentFileContext(trackRead = true) {
  const request = workspaceRequest, site = selectedSiteName.value, path = workspace.value?.path;
  const navigation = fileNavigationGeneration;
  return () => isFileViewActive() && navigation === fileNavigationGeneration && (!trackRead || request === workspaceRequest) && site === selectedSiteName.value && path === workspace.value?.path;
}
useNavigationGuard(() => {
  if (saving.value) return false;
  if (!isDirty.value) return true;
  return (async () => {
  const isCurrent = currentFileContext(false);
  let proceed = false;
  await confirm({
    title: t("panel.files.unsaved"), message: t("panel.files.unsavedHint"), tone: "neutral",
    impact: [workspace.value!.path],
    cancelLabel: t("panel.files.stay"), confirmLabel: t("panel.files.save"),
    alternativeLabel: t("panel.files.discard"),
    alternativeAction: () => { if (isCurrent()) { fileContent.value = savedContent.value; proceed = true; } },
    action: async () => { proceed = await saveCurrentFile(); }
  });
  return proceed;
  })();
});
function beforeUnload(event: BeforeUnloadEvent) {
  if (isDirty.value || saving.value) { event.preventDefault(); event.returnValue = ""; }
}
onMounted(() => window.addEventListener("beforeunload", beforeUnload));
onBeforeUnmount(() => { disposed = true; archiveState.value = null; workspaceRequest++; window.removeEventListener("beforeunload", beforeUnload); });
async function changeSite(event: Event) {
  if (!isFileViewActive()) return;
  const isCurrent = currentFileContext(false);
  const select = event.target as HTMLSelectElement;
  const next = select.value;
  select.value = selectedSiteName.value;
  if (await allowNavigation() && isCurrent()) {
    sitesStore.selectSite(next);
    await router.push({ query: { section: "files", site: next } });
  }
}
const backups = ref<BackupRecord[]>([]);
// Yedek listesi BİR KEZ başarıyla okundu mu. `refreshBackups` hata alınca listeyi BİLEREK
// boşaltır (bayat liste göstermemek için) — o boşluk "yedek yok" diye okunuyordu; aynı ekranın
// dosya bölümü bu ayrımı zaten yapıyordu (bkz. `workspaceLoad`).
const backupsLoaded = ref(false);

/**
 * SEÇİLİ SİTENİN yedekleri.
 *
 * Bu ekran bir alan adının içindedir (Web Siteleri → site → Dosyalar → Yedekler), ama liste
 * sunucudaki BÜTÜN yedekleri basıyordu: kullanıcı bir sitenin içindeyken başka sitelerin,
 * veritabanlarının ve panelin yedeklerini görüyordu. Bağlamda olmayan satırlar yalnızca
 * gürültü değil, yanlış satıra basma riskidir (geri yükleme yıkıcıdır).
 *
 * Süzgeç `SiteBackupView` ile AYNI kuraldır: tür "site" VE kaynak seçili site. Site
 * seçilmemişse (sunucu geneli Dosyalar ekranı) liste OLDUĞU GİBİ kalır — orada bağlam yoktur
 * ve süzmek her şeyi gizlerdi.
 */
const gorunenYedekler = computed(() =>
  selectedSiteName.value
    ? backups.value.filter((record) => record.type === "site" && record.source === selectedSiteName.value)
    : backups.value);

const backupSiteName = ref("");
const backupEngine = ref("mariadb");
const { notice, noticeError, showNotice, resolveApiMessage, clearNotice } = useNotice();
const searchQuery = ref("");
const searchResults = ref<FileEntry[]>([]);
const searchRan = ref(false);
const searchBusy = ref(false);
const searchError = ref("");
const searchSubmittedQuery = ref("");
const searchPath = ref("");
const searchTruncated = ref(false);
const searchTruncatedReason = ref("");
const searchScannedFiles = ref<number | null>(null);

function resetSearchResult() {
  searchRequest++;
  searchBusy.value = false;
  searchError.value = "";
  searchResults.value = [];
  searchRan.value = false;
  searchSubmittedQuery.value = "";
  searchPath.value = "";
  searchTruncated.value = false;
  searchTruncatedReason.value = "";
  searchScannedFiles.value = null;
}
function clearSearch() { searchQuery.value = ""; resetSearchResult(); }
watch(searchQuery, resetSearchResult, { flush: "sync" });

// Klasör tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const entries = computed(() => workspace.value?.entries ?? []);
const filePager = usePagination(entries);
const searchPager = usePagination(searchResults);
const selection = useBulkSelection();
const bulk = useBulkAction();
const deleteBusy = ref(false);
const visiblePaths = computed(() => filePager.items.value.map((entry) => entry.path));
const permissionPrincipal = ref("IIS_IUSRS");
const permissionRights = ref("M");
const createBusy = ref(false);
const permissionsBusy = ref(false);
const canSetPermissions = computed(() => isFileViewActive() && isAdmin.value && !locked.value &&
  !fileMutationBusy.value && !loading.value && !workspaceLoad.message.value &&
  Boolean(workspace.value && siteRootPath.value && workspace.value.path.toLowerCase() !== siteRootPath.value.toLowerCase()));

// TAŞI / KOPYALA hedef formu. `window.prompt` yerine ekranın kendi alanları: hedef listeden
// seçilebilir, üzerine yazma BİLİNÇLİ bir kutudur (varsayılan kapalı) ve iptal edilebilir.
const transferMode = ref<"move" | "copy" | null>(null);
const transferTarget = ref("");
const transferOverwrite = ref(false);
const transferBusy = ref(false);
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const siteRootPath = computed(() => selectedSiteName.value ? `sites/${selectedSiteName.value}` : "");
const canGoUp = computed(() =>
  Boolean(workspace.value?.parentPath) &&
  Boolean(siteRootPath.value) &&
  workspace.value!.path !== siteRootPath.value &&
  workspace.value!.parentPath.startsWith(siteRootPath.value)
);

/** Hedef listesindeki "üst klasör" seçeneği; site kökünün dışına çıkan bir üst yol gösterilmez. */
const parentTargetPath = computed(() => (canGoUp.value ? workspace.value!.parentPath : ""));

/**
 * YOL ÇUBUĞU parçaları — ilk parça HER ZAMAN site kökü.
 *
 * NEDEN KÖKÜN ÜSTÜ ÇİZİLMİYOR: sanal yol `sites/<site>/...` biçiminde; baştaki `sites` parçası
 * kullanıcının sanal alanının DIŞINDADIR ve `openPath` onu zaten reddeder. Tıklanamayacak bir
 * parçayı gezinilebilir göstermek, olmayan bir yetkiyi ima etmek olurdu.
 *
 * Bir DOSYA açıkken son parça dosyanın adıdır: o da tıklanmaz (zaten açık), ama üstündeki
 * klasörler tek tıkla erişilebilir kalır.
 */
const breadcrumb = computed(() => {
  const root = siteRootPath.value;
  const current = workspace.value?.path ?? "";
  if (!root || !current.startsWith(root)) {
    return [] as { name: string; path: string }[];
  }

  const parts = current.slice(root.length).split("/").filter(Boolean);
  const items = [{ name: selectedSiteName.value, path: root }];
  let birikmis = root;
  for (const part of parts) {
    birikmis = `${birikmis}/${part}`;
    items.push({ name: part, path: birikmis });
  }

  return items;
});

// ÇOKLU YÜKLEME durumu. `uploadTotal` yalnızca gösterim içindir; asıl kural sıraya alma:
// yirmi isteği paralel göndermek tarayıcının bağlantı havuzunu ve sunucuyu boşuna zorlar.
const uploadBusy = ref(false);
const fileMutationBusy = computed(() => archiveBusy.value || uploadBusy.value || saving.value || createBusy.value ||
  permissionsBusy.value || transferBusy.value || deleteBusy.value || bulk.busy.value);
const uploadDone = ref(0);
const uploadTotal = ref(0);
const uploadProgressLabel = computed(() =>
  t("panel.files.uploadProgress", { done: uploadDone.value, total: uploadTotal.value }));

/**
 * İlerleme çubuğunun doluluğu — DOSYA BAŞINA, bayt başına değil.
 *
 * Yükleme ucu bayt ilerlemesi bildirmiyor; oranı bayt üzerinden hesaplıyormuş gibi göstermek
 * kullanıcının kalan süreyi yanlış tahmin etmesine yol açardı. Toplam sıfırken 0 döner: sıfıra
 * bölüm `NaN` üretir ve çubuk genişliği `NaN%` ile hiç çizilmezdi.
 */
const uploadPercent = computed(() =>
  uploadTotal.value > 0 ? Math.round((uploadDone.value / uploadTotal.value) * 100) : 0);

/**
 * SATIR İÇİ İKİNCİL EYLEM ÇEKMECESİ — aynı anda YALNIZ BİR satır açık.
 *
 * Birden çok satırın açık kalması listeyi yeniden dağıtır ve kullanıcı hangi çekmecenin hangi
 * satıra ait olduğunu takip edemez; çekmece zaten ait olduğu satırın adını yazar.
 */
const openActionsPath = ref("");

function toggleActions(path: string) {
  openActionsPath.value = openActionsPath.value === path ? "" : path;
}

/**
 * SÜRÜKLE-BIRAK göstergesi.
 *
 * `dragleave` İÇ İÇE ögelerde de tetiklenir: fare karttan tabloya geçerken karta "ayrıldı"
 * denir ve gösterge kartın üstündeyken kapanır (ya da tersi, takılı kalır). Bu yüzden
 * enter/leave SAYILIR; sıfıra düşmeden gösterge kapanmaz.
 */
const dropActive = ref(false);
let dragDepth = 0;

// Klasör boyutu: yol -> ölçüm. Ölçülmeyen klasör sözlükte HİÇ yer almaz; "0" ile "ölçülmedi"
// birbirine karışmasın.
const folderSizes = ref<Record<string, DirectorySizeResult>>({});
const sizeBusyPath = ref("");
let sizeRequest = 0;

/**
 * Hedef olarak seçilebilecek alt klasörler. Taşınan/kopyalanan ögelerin KENDİLERİ listede yer
 * almaz: bir klasörü kendi içine taşımak arka uçta `files.move_into_itself` ile reddedilir,
 * kullanıcıya en baştan sunulmaması daha dürüst.
 */
const folderTargets = computed(() =>
  entries.value.filter((entry) => entry.isDirectory && !selection.isSelected(entry.path)));

onMounted(async () => {
  try {
    if (!sitesStore.sites.length) {
      await sitesStore.fetchSites();
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }

  const requestedSite = router.currentRoute.value.query.site;
  if (!props.embedded && typeof requestedSite === "string" && sitesStore.sites.some((site) => site.name === requestedSite)) sitesStore.selectSite(requestedSite);
  await Promise.all([
    selectedSiteName.value ? refresh(siteRootPath.value) : Promise.resolve(),
    refreshBackups()
  ]);
});

watch(() => router.currentRoute.value.query.site, (site) => {
  if (!props.embedded && router.currentRoute.value.query.section === "files" && typeof site === "string" && sitesStore.sites.some((item) => item.name === site)) sitesStore.selectSite(site);
});

watch(selectedSiteName, async (siteName, previousSiteName) => {
  if (siteName === previousSiteName) {
    return;
  }

  fileNavigationGeneration++;
  requestedWorkspacePath = "";
  archiveState.value = null;
  workspace.value = null;
  searchResults.value = [];
  searchRan.value = false;
  selection.clear();
  transferMode.value = null;
  if (siteName) {
    await refresh(`sites/${siteName}`);
  }
});

async function refresh(path = workspace.value?.path || siteRootPath.value) {
  if (!isFileViewActive() || !await allowNavigation() || !isFileViewActive()) return;
  const request = ++workspaceRequest;
  resetSearchResult();
  sizeRequest++;
  sizeBusyPath.value = "";
  const site = selectedSiteName.value;
  if (!selectedSiteName.value || !(path === siteRootPath.value || path.startsWith(`${siteRootPath.value}/`))) {
    workspace.value = null;
    workspaceLoad.clear();
    return;
  }

  if (path !== requestedWorkspacePath) {
    archiveState.value = null;
    requestedWorkspacePath = path;
    fileNavigationGeneration++;
  }
  loading.value = true;
  // Açık eylem çekmecesi de düşer: liste yeniden okunduğunda çekmecenin bağlı olduğu satır
  // silinmiş, taşınmış ya da başka bir sayfaya kaymış olabilir; açık kalan çekmece o an
  // BAŞKA bir ögenin altında duruyor gibi görünürdü.
  openActionsPath.value = "";
  // Ölçülmüş klasör boyutları BURADA düşer. Liste yeniden okunduğunda (yükleme, silme, taşıma,
  // başka klasöre geçiş) eski ölçüm artık bir şeyin karşılığı değildir; ekranda bırakmak
  // kullanıcıya bayat bir sayıyı güncel diye göstermek olurdu.
  folderSizes.value = {};
  try {
    const result = await getFileWorkspace(path);
    if (!isFileViewActive() || request !== workspaceRequest || site !== selectedSiteName.value) return;
    workspace.value = result;
    fileContent.value = result.content || "";
    savedContent.value = fileContent.value;
    workspaceLoad.clear();
    if (archiveOperation.value) {
      const operation = archiveOperation.value;
      operation.refreshError = "";
      if (operation.kind === "compress" && !operation.error) operation.output = archiveOutput(result, operation.sourcePath);
    }
    // Artık listede olmayan yollar seçili kalmasın (silme/klasör değişimi sonrası).
    selection.keepOnly(workspace.value.entries.map((entry) => entry.path));
    return true;
  } catch (error) {
    if (!isFileViewActive() || request !== workspaceRequest || site !== selectedSiteName.value) return;
    // Hata hem şeritte hem listenin yerinde görünür: boş bir tablo "klasör boş" sanılmasın.
    workspaceLoad.capture(error);
    if (archiveOperation.value) {
      archiveOperation.value.refreshError = t("panel.files.archiveRefreshFailed");
      workspaceLoad.message.value = archiveOperation.value.refreshError;
    }
    showNotice(resolveApiMessage(error), true);
    return false;
  } finally {
    if (request === workspaceRequest) loading.value = false;
  }
}

async function openPath(path: string) {
  if (!isFileViewActive() || !await allowNavigation() || !isFileViewActive()) return;
  if (!siteRootPath.value || !(path === siteRootPath.value || path.startsWith(`${siteRootPath.value}/`))) {
    return;
  }

  searchResults.value = [];
  searchRan.value = false;
  selection.clear();
  // Klasör değişince açık hedef formu ANLAMSIZ kalır: seçim gitti, hedef başka klasörün yolu.
  transferMode.value = null;
  filePager.reset();
  return await refresh(path);
}

async function uploadSelectedFiles(event: Event) {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files ?? []);
  try {
    await uploadMany(files, []);
  } finally {
    // Aynı dosya ikinci kez seçilebilsin: `input.value` temizlenmezse `change` hiç tetiklenmez.
    input.value = "";
  }
}

/**
 * ÇOKLU YÜKLEME — SIRAYLA, ve KISMİ BAŞARISIZLIK GİZLENMEZ.
 *
 * NEDEN SIRAYLA: yirmi `multipart` isteğini aynı anda açmak tarayıcının bağlantı havuzunu
 * tüketir, sunucuda yirmi eşzamanlı disk yazması başlatır ve ilerlemeyi ölçülemez kılar.
 *
 * NEDEN İLK HATADA DURULMUYOR: on dosyanın üçüncüsü kota/ad hatasıyla reddedildiğinde geri
 * kalan yedisini göndermemek kullanıcıyı işi baştan yapmaya zorlar. Döngü devam eder, özet
 * HANGİ dosyanın NEDEN olmadığını tek tek yazar (taşıma/kopyalama özetiyle aynı üslup).
 *
 * `folders` bırakılan ama YÜKLENEMEYEN klasörlerin adıdır: tarayıcı `input`/`drop` ile klasörü
 * dosya sanar ve içeriğini bu uçla gönderemez. Sessizce yutmak, kullanıcının sitesine
 * kopyalandığını sandığı bir klasörün hiç var olmaması demekti.
 */
async function uploadMany(files: File[], folders: string[]) {
  if (!isFileViewActive() || fileMutationBusy.value || !workspace.value?.isDirectory) {
    return;
  }

  // Sürükle-bırak `disabled` bilmez: askıdaki abonelikte istek zaten reddedilirdi, sebebi
  // BURADA söylenir — yoksa kullanıcı dosyayı bırakır ve hiçbir şey olmaz.
  if (locked.value) {
    showNotice(lockReason.value, true);
    return;
  }

  if (files.length === 0) {
    if (folders.length > 0) {
      showNotice(t("panel.files.uploadFolderSkipped", { names: folders.join(", ") }), true);
    }
    return;
  }

  const hedef = workspace.value.path;
  const isCurrent = currentFileContext(false);
  const isVisible = currentFileContext(false);
  files = [...files];
  folders = [...folders];
  const failures: { name: string; reason: string }[] = [];
  uploadBusy.value = true;
  uploadDone.value = 0;
  uploadTotal.value = files.length;

  try {
    for (const file of files) {
      if (!isCurrent() || locked.value) return;
      try {
        dismissArchiveOperation();
        await uploadFile(hedef, file);
        if (!isCurrent()) return;
        uploadDone.value += 1;
      } catch (error) {
        if (!isCurrent()) return;
        failures.push({ name: file.name, reason: resolveApiMessage(error) });
      }
    }
    if (!isCurrent()) return;
    const refreshed = await refreshAfterFileOperation(hedef, isVisible);
    if (!isVisible() || refreshed === undefined) return;
    reportFileOperation(uploadOzeti(files.length, uploadDone.value, failures, folders), failures.length > 0 || folders.length > 0, refreshed);
  } finally { uploadBusy.value = false; }
}

function reportFileOperation(message: string, failed: boolean, refreshed: boolean) {
  showNotice([message, refreshed ? "" : t("panel.files.refreshAfterOperationFailed")].filter(Boolean).join("\n\n"), failed || !refreshed);
}

async function refreshAfterFileOperation(path: string, isVisible: () => boolean) {
  const refreshed = await refresh(path);
  if (refreshed === false && isVisible()) {
    workspaceLoad.message.value = t("panel.files.refreshAfterOperationFailed");
  }
  return refreshed;
}

/** Yükleme özeti — "tamamlandı" yalnız GERÇEKTEN hepsi olduğunda yazılır. */
function uploadOzeti(total: number, done: number, failures: { name: string; reason: string }[], folders: string[]) {
  const lines = failures.map((satir) => t("panel.bulk.failedItem", { name: satir.name, reason: satir.reason }));
  if (folders.length > 0) {
    lines.push(t("panel.files.uploadFolderSkipped", { names: folders.join(", ") }));
  }

  if (lines.length === 0) {
    return t("panel.files.uploadedAll", { total });
  }

  return [t("panel.files.uploadedPartial", { total, done, failed: failures.length }), ...lines].join("\n");
}

/** Sürüklenen şey DOSYA mı: metin/bağlantı sürüklerken bırakma alanı açılmamalı. */
function hasFiles(event: DragEvent) {
  return Array.from(event.dataTransfer?.types ?? []).includes("Files");
}

function onDragEnter(event: DragEvent) {
  if (!hasFiles(event) || !workspace.value?.isDirectory) {
    return;
  }

  event.preventDefault();
  dragDepth += 1;
  dropActive.value = true;
}

function onDragOver(event: DragEvent) {
  if (!hasFiles(event) || !workspace.value?.isDirectory) {
    return;
  }

  // preventDefault ŞART: yapılmazsa tarayıcı bırakmayı reddeder ve dosyayı sekmede açar.
  event.preventDefault();
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = "copy";
  }
}

function onDragLeave(event: DragEvent) {
  if (!hasFiles(event)) {
    return;
  }

  event.preventDefault();
  dragDepth -= 1;
  if (dragDepth <= 0) {
    dragDepth = 0;
    dropActive.value = false;
  }
}

/**
 * BIRAKMA. Gösterge her koşulda kapanır (sayaç sıfırlanır), yükleme başarısız olsa bile.
 *
 * KLASÖR AYIKLAMA `webkitGetAsEntry` ile yapılır — dosya adına/boyutuna bakan tahminler
 * (uzantısız dosyaları klasör sanmak) yanlış cevap verir. Öge listesi EŞ ZAMANLI okunur:
 * `DataTransfer` ilk `await`ten sonra boşaltılır, sonra okunursa hiçbir dosya bulunamaz.
 */
async function onDrop(event: DragEvent) {
  if (!hasFiles(event) || !workspace.value?.isDirectory) {
    return;
  }

  event.preventDefault();
  dragDepth = 0;
  dropActive.value = false;

  const transfer = event.dataTransfer;
  if (!transfer) {
    return;
  }

  const files: File[] = [];
  const folders: string[] = [];
  const items = Array.from(transfer.items ?? []);
  if (items.length > 0 && typeof items[0].webkitGetAsEntry === "function") {
    for (const item of items) {
      if (item.kind !== "file") {
        continue;
      }

      const entry = item.webkitGetAsEntry();
      if (entry?.isDirectory) {
        folders.push(entry.name);
        continue;
      }

      const file = item.getAsFile();
      if (file) {
        files.push(file);
      }
    }
  } else {
    // Eski tarayıcı: klasör ayırt edilemez. Yine de dosyalar gönderilir; klasör sunucuda
    // reddedilir ve sebebi özette görünür — sessiz kayıp olmaz.
    files.push(...Array.from(transfer.files ?? []));
  }

  await uploadMany(files, folders);
}

/**
 * KLASÖR BOYUTU — sonuç EKSİKSE bunu SÖYLER.
 *
 * Sunucu öge/süre bütçesini aşarsa taramayı keser (`truncated`). O hâlde gösterilen değer bir
 * ALT SINIRDIR; tam sayı gibi göstermek kullanıcının disk kararını yanlış sayıya dayandırması
 * olurdu. Bu yüzden hem satırda "en az" denir hem de şeritte sebep (çeviri anahtarı) yazılır.
 */
async function calculateFolderSize(entry: FileEntry) {
  if (!isFileViewActive() || !entry.isDirectory || sizeBusyPath.value || loading.value || workspaceLoad.message.value) {
    return;
  }
  entry = { ...entry };
  const isCurrent = currentFileContext();
  const request = ++sizeRequest;
  sizeBusyPath.value = entry.path;
  // A failed new measurement must not leave the previous size looking current.
  delete folderSizes.value[entry.path];
  try {
    const result = await getDirectorySize(entry.path);
    if (!isCurrent() || request !== sizeRequest) return;
    folderSizes.value = { ...folderSizes.value, [entry.path]: result };
    const olcum = t("panel.files.sizeSummary", {
      name: entry.name,
      size: boyutMetni(result.totalBytes),
      files: result.fileCount,
      folders: result.directoryCount
    });
    showNotice(
      result.truncated ? `${olcum}\n${kesikSebebi(result)}` : olcum,
      result.truncated);
  } catch (error) {
    if (isCurrent() && request === sizeRequest) showNotice(resolveApiMessage(error), true);
  } finally {
    if (request === sizeRequest) sizeBusyPath.value = "";
  }
}

/** Ölçümün NEDEN eksik kaldığı; sebep anahtarı çevrilir, yoksa anahtarın kendisi görünür. */
function kesikSebebi(result: DirectorySizeResult) {
  return t("panel.files.sizeTruncated", { reason: hataMetni(result.truncatedReasonKey) });
}

/** Bayt biçimi panelin geri kalanıyla AYNI (bkz. utils/bytes.ts); ekran kendi biçimini uydurmaz. */
function boyutMetni(value: number) {
  return formatBytes(value, t);
}

/**
 * Değiştirilme tarihi.
 *
 * VERİ ZATEN GELİYORDU: `FileEntry.lastModifiedUtc` her satır için sunucudan okunuyor ama ekranda
 * hiç gösterilmiyordu — oysa "hangi dosya en son değişti" bir dosya yöneticisinin en sık sorulan
 * sorusudur (bozulan bir siteyi teşhis ederken ilk bakılan sütun).
 *
 * BİÇİM UYDURULMUYOR: gövde `utils/datetime.ts`teki TEK kaynaktan gelir (panelin 24 ekranındaki
 * `toLocaleString()` kopyaları oraya toplandı). Burada i18n yereline geçmek ya da kendi biçimini
 * yazmak, panelde İKİNCİ bir tarih biçimi yaratırdı; tek biçim, tutarlı biçimdir.
 *
 * Boş değer için panelin ortak "yok" karşılığı geçilir: çizgi karakteri ekran okuyucuda hiçbir
 * şey söylemez.
 */
function tarihMetni(value: string | null) {
  return formatDateTime(value, t("panel.common.notAvailable"));
}

async function runSearch() {
  if (!isFileViewActive() || !workspace.value?.isDirectory || loading.value || workspaceLoad.message.value || searchQuery.value.trim().length < 2 || searchBusy.value) return;
  const request = ++searchRequest;
  const isCurrent = currentFileContext();
  const path = workspace.value.path, query = searchQuery.value.trim();
  searchBusy.value = true;
  searchError.value = "";
  searchRan.value = false;
  searchResults.value = [];
  searchSubmittedQuery.value = query;
  searchPath.value = path;
  searchTruncated.value = false;
  searchTruncatedReason.value = "";
  searchScannedFiles.value = null;
  try {
    const result = await searchFiles(path, query);
    if (!isCurrent() || request !== searchRequest) return;
    searchResults.value = result.items;
    searchTruncated.value = result.truncated;
    searchTruncatedReason.value = result.truncatedReasonKey;
    searchScannedFiles.value = result.scannedFiles;
    searchPager.reset();
    // "Sonuç yok" ile "henüz aranmadı" ayrı: boş sonuç listesi sessizce kaybolmasın.
    searchRan.value = true;
  } catch (error) {
    if (!isCurrent() || request !== searchRequest) return;
    searchResults.value = [];
    searchRan.value = false;
    searchError.value = resolveApiMessage(error);
  } finally { if (request === searchRequest) searchBusy.value = false; }
}

async function compress(path: string) {
  await runArchive(path, false);
}

async function extract(path: string) {
  await runArchive(path, true);
}

async function runArchive(path: string, extracting: boolean) {
  if (!isFileViewActive() || fileMutationBusy.value || locked.value || loading.value || workspaceLoad.message.value || !workspace.value?.isDirectory) return;
  const isCurrent = currentFileContext(false);
  const destination = workspace.value.path;
  const previous = archiveOperation.value, previousContext = archiveContext;
  const outputPath = `${path.replace(/[\\/]+$/, "")}.zip`;
  const collision = !extracting && workspace.value.entries.some(entry => filePathKey(entry.path) === filePathKey(outputPath));
  archiveBusy.value = true;
  archiveContext = isCurrent;
  archiveState.value = { kind: extracting ? "extract" : "compress", sourcePath: path, destinationPath: destination,
    status: extracting || collision ? "confirming" : "running", error: "", refreshError: "", output: null };
  try {
    if (extracting || collision) {
      const name = path.replace(/\\/g, "/").split("/").pop()!;
      const approved = await confirm({
        title: t(extracting ? "panel.files.extractConfirmTitle" : "panel.files.compressOverwriteTitle"),
        message: t(extracting ? "panel.files.extractConfirmMessage" : "panel.files.compressOverwriteMessage", { name, path: extracting ? destination : outputPath }),
        impact: [path, extracting ? destination : outputPath], tone: "danger",
        confirmLabel: t(extracting ? "panel.files.extractConfirmAction" : "panel.files.compressOverwriteAction")
      });
      if (!approved || !isCurrent() || locked.value) {
        archiveState.value = previousContext?.() ? previous : null;
        archiveContext = previousContext;
        return;
      }
    }
    archiveState.value!.status = "running";
    clearNotice();
    const result = await (extracting ? extractPath(path) : compressPath(path, "", collision));
    if (!isCurrent()) return;
    // The mutation response owns this newer workspace; older list reads cannot
    // put the pre-operation directory back after publication.
    workspaceRequest++;
    loading.value = false;
    workspaceLoad.clear();
    resetSearchResult();
    sizeRequest++;
    sizeBusyPath.value = "";
    folderSizes.value = {};
    openActionsPath.value = "";
    workspace.value = result;
    fileContent.value = result.content;
    savedContent.value = result.content;
    selection.keepOnly(result.entries.map(entry => entry.path));
    archiveState.value!.output = extracting ? null : archiveOutput(result, path);
    archiveState.value!.status = "succeeded";
    showNotice(t(extracting ? "panel.files.extracted" : "panel.files.compressed"), false);
  } catch (error) {
    if (!isCurrent()) return;
    const message = resolveApiMessage(error);
    archiveState.value!.error = message;
    archiveState.value!.status = "refreshing";
    // Earlier extracted files or the published ZIP may remain despite a failed response.
    // Remeasure the directory without claiming that those changes were rolled back.
    await refresh(destination);
    if (!isCurrent()) return;
    archiveState.value!.status = "failed";
    showNotice(message, true);
  } finally { archiveBusy.value = false; }
}

async function refreshArchiveListing() {
  const operation = archiveOperation.value, isCurrent = archiveContext;
  if (!operation || !isCurrent || fileMutationBusy.value || loading.value ||
    (operation.status !== "succeeded" && operation.status !== "failed")) return;
  const status = operation.status;
  archiveBusy.value = true;
  operation.status = "refreshing";
  try {
    await refresh(operation.destinationPath);
    if (!isCurrent()) return;
    operation.status = status;
    if (operation.error) showNotice(operation.error, true);
  } finally { archiveBusy.value = false; }
}

function dismissArchiveOperation() {
  if (archiveOperation.value?.status === "succeeded" || archiveOperation.value?.status === "failed") archiveState.value = null;
}

async function savePermissions() {
  if (!canSetPermissions.value || !workspace.value) return;
  const isCurrent = currentFileContext(false);
  const path = workspace.value.path, principal = permissionPrincipal.value, rights = permissionRights.value;
  permissionsBusy.value = true;
  try {
    dismissArchiveOperation();
    const result = await updatePermissions(path, principal, rights);
    if (!isCurrent()) return;
    if (result.permissionsVerified === true) showNotice(t("panel.files.permissionsSaved"), false);
    else showNotice([t("panel.files.permissionsUnverified"), result.permissionsDetail].filter(Boolean).join("\n\n"), true);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally { permissionsBusy.value = false; }
}

/*
 * Aşağıdaki dört işlemin hiçbirinde hata yakalama YOKTU: sunucu reddettiğinde (izin yok, disk
 * dolu, yol kilitli) kullanıcı EKRANDA HİÇBİR ŞEY görmüyordu.
 *
 * En pahalısı "kaydet"ti: kullanıcı dosyayı düzenler, Kaydet'e basar, istek başarısız olur,
 * hiçbir uyarı çıkmaz — kaydedildiğini sanıp sekmeyi kapatır ve emeği kaybolur. Sessiz
 * başarısızlık burada doğrudan VERİ KAYBIDIR.
 */
async function createFolder() {
  if (!isFileViewActive() || locked.value || fileMutationBusy.value || loading.value || workspaceLoad.message.value || !workspace.value?.isDirectory) return;
  const isCurrent = currentFileContext(false);
  const parentPath = workspace.value.path;
  createBusy.value = true;
  try {
    const folderName = await prompt({
      title: t("panel.files.folderPrompt"),
      message: t("panel.files.createTarget", { path: parentPath }),
      label: t("panel.files.name"),
      defaultValue: "new-folder"
    });
    if (!folderName?.trim() || !isCurrent() || locked.value || workspaceLoad.message.value) return;
    const basePath = `${parentPath}/${folderName.trim()}`;
    dismissArchiveOperation();
    await createDirectory(basePath);
    if (!isCurrent()) return;
    const refreshed = await refreshAfterFileOperation(parentPath, isCurrent);
    if (!isCurrent() || refreshed === undefined) return;
    reportFileOperation(t("panel.files.folderCreated"), false, refreshed);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally { createBusy.value = false; }
}

/**
 * BOŞ DOSYA OLUŞTUR — panelin en çok istenen eksiğiydi: siteye `.htaccess`, `robots.txt` ya da
 * `web.config` koymanın tek yolu dosyayı dışarıda yazıp YÜKLEMEKTİ.
 *
 * ÜZERİNE YAZMA KORUMASI: ad önce listede aranır; createFile ayrıca FailIfExists gönderir.
 * Sunucudaki CreateNew, iki oturum aynı anda oluştursa da mevcut dosyanın ezilmesini engeller.
 */
async function createNewFile() {
  if (!isFileViewActive() || locked.value || fileMutationBusy.value || loading.value || workspaceLoad.message.value || !workspace.value?.isDirectory) return;
  const isCurrent = currentFileContext(false);
  const parentPath = workspace.value.path;
  createBusy.value = true;
  try {
    const fileName = await prompt({
      title: t("panel.files.filePrompt"),
      message: t("panel.files.createTarget", { path: parentPath }),
      label: t("panel.files.name"),
      placeholder: ".htaccess"
    });
    if (!fileName?.trim() || !isCurrent() || locked.value || workspaceLoad.message.value) return;
    const name = fileName.trim();
    // Ad bir YOL olamaz: "alt/klasor/dosya" ya da ".." arka uçta sanal alan denetimine takılır;
    // kullanıcı bunu isteği göndermeden önce öğrensin.
    if (/[\\/:*?"<>|]/.test(name)) {
      showNotice(t("files.invalid_name"), true);
      return;
    }

    if (entries.value.some((entry) => entry.name.toLowerCase() === name.toLowerCase())) {
      showNotice(t("panel.files.fileExists"), true);
      return;
    }

    const basePath = `${parentPath}/${name}`;
    dismissArchiveOperation();
    await createFile(basePath);
    if (!isCurrent()) return;
    // Dosya oluşturulup DÜZENLEYİCİDE açılır: "oluştur" sonrası kullanıcı ayrıca aramak
    // zorunda kalmasın.
    const refreshed = await openPath(basePath);
    // openPath owns the destination read and returns no result for a stale request.
    if (!isFileViewActive() || refreshed === undefined) return;
    if (!refreshed) workspaceLoad.message.value = t("panel.files.refreshAfterOperationFailed");
    reportFileOperation(t("panel.files.fileCreated"), false, refreshed);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally { createBusy.value = false; }
}

/**
 * YENİDEN ADLANDIR — hedefi ögenin kendi klasörü olan tek ögelik taşıma (bkz. api/files.ts).
 *
 * Sonuç öge başına gelir: istek 200 dönse bile öge taşınmamış olabilir (hedefte aynı ad var,
 * dosya kullanımda...). Bu yüzden "yeniden adlandırıldı" yalnız `moved` doğruyken yazılır.
 */
async function renamePath(entry: FileEntry) {
  if (!isFileViewActive() || locked.value || fileMutationBusy.value) return;
  entry = { ...entry };
  const isCurrent = currentFileContext(false);
  const isVisible = currentFileContext(false);
  const sourceFolder = workspace.value?.path || "";
  transferBusy.value = true;
  try {
    const newName = await prompt({
      title: t("panel.files.renamePrompt"),
      label: t("panel.files.name"),
      defaultValue: entry.name
    });
    if (!newName || newName.trim() === entry.name || !isCurrent() || locked.value) return;
    dismissArchiveOperation();
    const result = await renameEntry(entry.path, newName.trim(), false);
    if (!isCurrent()) return;
    const refreshed = await refreshAfterFileOperation(sourceFolder, isVisible);
    if (!isVisible() || refreshed === undefined) return;
    const failure = result.items.find((item) => !item.moved);
    if (failure) {
      reportFileOperation(t("panel.bulk.failedItem", { name: entry.name, reason: hataMetni(failure.errorKey) }), true, refreshed);
      return;
    }

    reportFileOperation(t("panel.files.renamed"), false, refreshed);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally {
    transferBusy.value = false;
  }
}

/** Hedef formunu açar; hedef varsayılan olarak BULUNULAN klasördür, kullanıcı değiştirir. */
function startTransfer(mode: "move" | "copy") {
  if (selection.count.value === 0) {
    return;
  }

  transferMode.value = mode;
  transferTarget.value = workspace.value?.path || siteRootPath.value;
  transferOverwrite.value = false;
}

/**
 * TAŞI / KOPYALA — KISMİ BAŞARISIZLIK YALANLANMAZ.
 *
 * Uçlar tek istekte çoklu öge alır ve öge başına sonuç döner. "Tamamlandı" demek, taşınamayan iki
 * dosyayı sessizce kaybetmek olurdu; özet kaçının olduğunu, kaçının olmadığını ve HANGİSİNİN NEDEN
 * olmadığını (`errorKey` → kullanıcının dili) satır satır yazar.
 */
async function runTransfer() {
  const mode = transferMode.value;
  const targets = entries.value.filter((entry) => selection.isSelected(entry.path));
  if (!isFileViewActive() || locked.value || fileMutationBusy.value || !mode || targets.length === 0) {
    return;
  }

  const destination = transferTarget.value.trim().replace(/\\/g, "/").replace(/\/+$/, "");
  // Site kökünün dışı arka uçta zaten reddedilir; burada da söylenir ki kullanıcı hatayı
  // istek gitmeden görsün. Anahtar arka ucun kullandığıyla AYNI: iki yerde iki farklı cümle olmaz.
  if (!destination || !siteRootPath.value || !(destination === siteRootPath.value || destination.startsWith(`${siteRootPath.value}/`))) {
    showNotice(t("files.path_outside_root"), true);
    return;
  }

  const paths = targets.map((entry) => entry.path);
  const overwrite = transferOverwrite.value;
  const sourceFolder = workspace.value?.path || "";
  const isCurrent = currentFileContext(false);
  const isVisible = currentFileContext(false);
  transferBusy.value = true;
  try {
    const failures: { name: string; reason: string }[] = [];
    // UYARI ≠ HATA: kopyalamada bir alt öge bağlantı (junction/symlink) olduğu için atlanmışsa
    // öge "kopyalandı" sayılır ama kopya EKSİKTİR (bkz. CopyFileResultDto.ErrorKey). Bu satırı
    // yutmak, kullanıcının eksik kopyayı tam sanmasına yol açardı.
    const warnings: { name: string; reason: string }[] = [];
    let done = 0;

    dismissArchiveOperation();
    if (mode === "move") {
      const result = await moveEntries(paths, destination, overwrite);
      if (!isCurrent()) return;
      done = result.movedCount;
      for (const item of result.items.filter((satir) => !satir.moved)) {
        failures.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
      }
    } else {
      const result = await copyEntries(paths, destination, overwrite);
      if (!isCurrent()) return;
      done = result.copiedCount;
      for (const item of result.items) {
        if (!item.copied) {
          failures.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
        } else if (item.errorKey) {
          warnings.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
        }
      }
    }

    transferMode.value = null;
    selection.clear();
    const refreshed = await refreshAfterFileOperation(sourceFolder, isVisible);
    if (!isVisible() || refreshed === undefined) return;
    reportFileOperation(
      transferOzeti(mode, targets.length, done, failures, warnings),
      failures.length > 0 || warnings.length > 0, refreshed);
  } catch (error) {
    // İstek TÜMÜYLE reddedildi (hedef klasör yok, sanal alan dışı...): form açık kalır ki
    // kullanıcı hedefi düzeltip yeniden deneyebilsin.
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally {
    transferBusy.value = false;
  }
}

/** Sunucunun döndürdüğü hata ANAHTARINI kullanıcının diline çevirir; karşılığı yoksa anahtarın
 *  kendisi görünür — sessizce boş bırakmaktan iyidir (bkz. composables/useNotice.ts). */
function hataMetni(errorKey: string) {
  if (!errorKey) {
    return t("panel.files.reasonUnknown");
  }

  const translated = t(errorKey);
  return translated === errorKey ? errorKey : translated;
}

/**
 * Taşıma/kopyalama özeti — ÜÇ DURUM AYRI: tamamı oldu / bir kısmı olmadı / hepsi oldu ama kopya
 * eksik. Üçünü tek "tamamlandı" cümlesine indirmek, kullanıcıya olmayan bir sonucu bildirmektir.
 */
function transferOzeti(
  mode: "move" | "copy",
  total: number,
  done: number,
  failures: { name: string; reason: string }[],
  warnings: { name: string; reason: string }[]
) {
  const lines = [...failures, ...warnings].map((satir) =>
    t("panel.bulk.failedItem", { name: satir.name, reason: satir.reason }));

  if (failures.length === 0 && warnings.length === 0) {
    return mode === "move"
      ? t("panel.files.movedAll", { total })
      : t("panel.files.copiedAll", { total });
  }

  if (failures.length === 0) {
    return [t("panel.files.copiedIncomplete", { total }), ...lines].join("\n");
  }

  const head = mode === "move"
    ? t("panel.files.movedPartial", { total, done, failed: failures.length })
    : t("panel.files.copiedPartial", { total, done, failed: failures.length });
  return [head, ...lines].join("\n");
}

/**
 * Silme ONAYSIZ yapılmaz.
 *
 * Arkasında `Directory.Delete(recursive: true)` var: klasörde tek tıkla BÜTÜN ağaç gider ve
 * geri dönüşü yoktur. Bu yüzden klasörlerde `requireText` ile klasör adının yazılması istenir;
 * dosyada normal onay yeter. Onay metni NEYİN silineceğini (tam yol) açıkça söyler.
 */
async function removePath(entry: FileEntry) {
  if (!isFileViewActive() || locked.value || fileMutationBusy.value) return;
  entry = { ...entry };
  const isFolder = entry.isDirectory;
  const isCurrent = currentFileContext(false);
  const sourceFolder = workspace.value?.path || "";
  deleteBusy.value = true;
  try {
    await confirm({
      title: isFolder ? t("panel.files.confirmDeleteFolderTitle") : t("panel.files.confirmDeleteFileTitle"),
      message: isFolder
        ? t("panel.files.confirmDeleteFolderMessage", { name: entry.name })
        : t("panel.files.confirmDeleteFileMessage", { name: entry.name }),
      impact: [entry.path],
      // Klasör = geri dönüşsüz özyinelemeli silme: adın birebir yazılması istenir.
      requireText: isFolder ? entry.name : undefined,
      action: async () => {
        if (!isCurrent() || locked.value) return;
        try {
          dismissArchiveOperation();
          await deletePath(entry.path);
          if (!isCurrent()) return;
          const refreshed = await refreshAfterFileOperation(sourceFolder, isCurrent);
          if (!isCurrent() || refreshed === undefined) return;
          reportFileOperation(t("panel.files.deleted"), false, refreshed);
        } catch (error) {
          if (isCurrent()) showNotice(resolveApiMessage(error), true);
        }
      }
    });
  } finally {
    deleteBusy.value = false;
  }
}

/**
 * TOPLU SİLME — ONAY ZORUNLU, KISMİ BAŞARISIZLIK SESSİZ KALMAZ.
 *
 * Onay kaç öğenin gideceğini ve HANGİLERİNİ (tam yol listesi) yazar. Seçimde klasör varsa
 * arkasında özyinelemeli silme vardır; bu yüzden öğe SAYISININ birebir yazılması istenir —
 * "8 öğe" görüp refleksle onaylamak mümkün olmasın.
 *
 * Silme tek uçlu (`DELETE /files`), toplu ucu YOK: öğeler tek tek denenir. Biri başarısız
 * olursa döngü durmaz; özet "kaçı silindi, kaçı silinemedi ve neden" der.
 */
async function removeSelected() {
  if (!isFileViewActive() || locked.value || fileMutationBusy.value) return;
  const isCurrent = currentFileContext(false);
  const sourceFolder = workspace.value?.path || "";
  const targets = entries.value.filter((entry) => selection.isSelected(entry.path)).map(entry => ({ ...entry }));
  if (targets.length === 0) {
    return;
  }

  const folderCount = targets.filter((entry) => entry.isDirectory).length;
  const impact = bulk.impactList(targets.map((entry) => entry.path));
  if (folderCount > 0) {
    impact.unshift(t("panel.files.bulkFolderWarn", { count: folderCount }));
  }

  deleteBusy.value = true;
  try {
    const onay = await confirm({
      title: t("panel.bulk.confirmDeleteTitle", { count: targets.length }),
      message: t("panel.bulk.confirmDeleteMessage", { count: targets.length }),
      impact,
      requireText: String(targets.length)
    });
    if (!onay || !isCurrent() || locked.value) return;

    dismissArchiveOperation();
    const outcome = await bulk.runBulk(targets, (entry) => entry.path, (entry) => deletePath(entry.path), () => isCurrent() && !locked.value);
    if (!isCurrent()) return;
    const refreshed = await refreshAfterFileOperation(sourceFolder, isCurrent);
    if (!isCurrent() || refreshed === undefined) return;
    reportFileOperation(bulk.deleteSummary(outcome), outcome.failures.length > 0, refreshed);
  } finally {
    deleteBusy.value = false;
  }
}

async function saveCurrentFile(): Promise<boolean> {
  if (!isFileViewActive() || !workspace.value || !canEditFile.value || fileMutationBusy.value || loading.value || locked.value) return false;
  const path = workspace.value.path;
  const content = fileContent.value;
  const isCurrent = currentFileContext(false);
  saving.value = true;
  try {
    dismissArchiveOperation();
    await saveFile(path, content);
    if (!isCurrent()) return false;
    savedContent.value = content;
    showNotice(t("panel.files.saved"), false);
    return true;
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
    return false;
  } finally { saving.value = false; }
}

async function refreshBackups() {
  try {
    backups.value = await getBackups();
    backupsLoaded.value = true;
    fullRestore.clearRefreshError();
    return true;
  } catch (error) {
    // Yedek listesi okunamadıysa ESKİ liste bırakılmaz: bayat bir listeyi güncel sanmak,
    // olmayan bir yedeğe güvenmek demektir. Boşaltılan liste "yedek yok" DEĞİLDİR: bayrak da
    // geri alınır, yoksa temizlenen liste kesin bir cümleye dönüşürdü.
    backups.value = [];
    backupsLoaded.value = false;
    showNotice(resolveApiMessage(error), true);
    return false;
  }
}

async function createSiteBackup() {
  try {
    await backupSite(backupSiteName.value);
    await refreshBackups();
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function createDatabaseBackup() {
  try {
    await backupDatabase(backupEngine.value);
    await refreshBackups();
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function restore(record: BackupRecord) {
  await fullRestore.restore(record, (result) => {
    const report = restoreResult(result, t);
    showNotice(t(report.failed ? "panel.backup.restoredWithErrors" : "panel.backup.restored"), report.failed);
  }, (error) => showNotice(error instanceof Error && !(error instanceof UserFacingError) && !('isAxiosError' in error) ? error.message : resolveApiMessage(error), true), refreshBackups);
}

</script>

<style scoped>
.files-panel,
.workspace-card {
  display: grid;
  gap: 12px;
}

.workspace-card {
  container: file-workspace / inline-size;
  min-width: 0;
}

.archive-state {
  --archive-tone: var(--accent-1);
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
  padding: var(--sp-16);
  border: 1px solid color-mix(in srgb, var(--archive-tone) 35%, var(--panel-border));
  border-left: 3px solid var(--archive-tone);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--archive-tone) 4%, var(--surface-1));
}
.archive-state.is-error { --archive-tone: var(--warn-1); }
.archive-state.is-success { --archive-tone: var(--ok-1); }
.archive-heading { display: flex; align-items: flex-start; gap: var(--sp-12); }
.archive-heading h3 { flex: 1; min-width: 0; margin: 0; font-size: var(--fs-sm); overflow-wrap: anywhere; }
.archive-symbol { flex: 0 0 22px; width: 22px; height: 22px; color: var(--archive-tone); }
.archive-scope { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-12); margin: 0; }
.archive-scope > div { min-width: 0; }
.archive-scope dt { color: var(--text-muted); font-size: var(--fs-xs); margin-bottom: var(--sp-4); }
.archive-scope dd { margin: 0; color: var(--text-strong); font-size: var(--fs-xs); overflow-wrap: anywhere; }
.archive-state p { margin: 0; line-height: 1.6; overflow-wrap: anywhere; }
.archive-error { color: var(--text-strong); font-size: var(--fs-sm); }
.archive-actions { display: flex; align-items: center; gap: var(--sp-8); flex-wrap: wrap; }
.archive-output { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-16); flex-wrap: wrap; min-width: 0; padding: var(--sp-12); background: var(--surface-1); border: 1px solid var(--panel-border); border-radius: var(--radius-sm); }
.archive-output-details { display: grid; gap: var(--sp-4); min-width: 0; flex: 1 1 180px; }
.archive-output-details strong { font-size: var(--fs-sm); color: var(--text-strong); overflow-wrap: anywhere; }
.archive-output .action-link { display: inline-flex; align-items: center; gap: var(--sp-8); max-width: 100%; white-space: normal; }
.archive-output svg { width: 18px; height: 18px; flex: 0 0 18px; }
@container file-workspace (max-width: 480px) {
  .archive-scope { grid-template-columns: minmax(0, 1fr); }
  .archive-actions button { max-width: 100%; white-space: normal; }
}

.mobile-select-label { display: none; }

.site-picker {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.site-picker select {
  width: min(260px, 48vw);
}

.empty-state {
  /* `place-content` YALNIZCA ızgara/esnek kapsayıcıda çalışır. Kutu blok olduğu için dikey
     ortalama hiç uygulanmıyordu ve 180px'lik kutuda metin tepede asılı kalıyordu.
     `align-content` (yalnız dikey) kullanılıyor, `place-content` (iki eksen) DEĞİL: ikincisi
     yatayda da ortalar, bu da sütunu `max-content` genişliğine kilitler ve uzun bir boş-durum
     cümlesi dar ekranda kutunun dışına taşardı. */
  display: grid;
  min-height: 180px;
  align-content: center;
  text-align: center;
}

.action-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
}

.files-row,
.backup-row,
.form-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

.files-row {
  /* SÜTUN BÜTÇESİ YENİDEN DAĞITILDI. Eski satır: 30+200+100+120+430 + 4×12px boşluk = 928px
     taban ve bunun 430px'i (%46) yalnızca düğmelere ayrılmıştı. İkincil eylemler çekmeceye
     alınınca işlemler sütunu 140px'e indi; açılan yerin bir kısmıyla ARTIK VAR OLAN ama hiç
     gösterilmeyen "Değiştirilme" verisi bir sütun oldu — altı sütunla taban yine de
     30+200+80+100+140+140 + 5×12 = 750px, yani eskisinden 178px dar.
     Boyut sütunu dar tutulabilir: "1,2 GiB"ten uzun bir değer yok. */
  grid-template-columns: 30px minmax(200px, 1fr) 80px 100px 140px 140px;
}

/* SAYILAR SAĞA. Sola hizalı boyutlar farklı basamak sayılarında birbirini tutmuyor ve iki satırı
   karşılaştırmak için okumak gerekiyordu; `tabular-nums` ile rakamlar eşit genişlikte olduğu için
   sütun bir cetvel gibi okunur. Başlık da aynı hizaya alınır, yoksa başlık verinin üstünde
   durmaz. */
.size-cell {
  color: var(--text-muted);
  white-space: nowrap;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

/* Ölçüm düğmesi bir VERİ hücresinin içinde durur: düğme gibi değil, yerine geçtiği "—" gibi
   sessiz görünmeli, ama tıklanabilir olduğu da anlaşılmalı. */
.size-calc {
  width: auto;
  color: var(--accent-1);
  font-size: 0.82rem;
  font-weight: 600;
}

.date-cell {
  color: var(--text-muted);
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

/* Hizalama sınıfları başlık satırında da gerekli (başlık verinin üstünde durmalı), ama
   SÖNÜKLEŞTİRME gerekli değil: başlık satırının kendi rengi var. */
.table-head .size-cell,
.table-head .date-cell {
  color: inherit;
}

.entry-name {
  font-weight: 600;
}

/* Yol çubuğu tek satırda kalır; derin yollarda yatay kaydırılır (satır kırılıp araç
   düğmelerini aşağı itmesin). */
.breadcrumb {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  min-width: 0;
}

.breadcrumb-sep {
  color: var(--text-muted);
}

/* Bırakma alanı kartın ÜSTÜNE biner: kullanıcı hedefi görmeden bırakmasın. `pointer-events`
   kapalı, yoksa katmanın kendisi `dragleave` üretip göstergeyi titretirdi. */
.workspace-card {
  position: relative;
}

.workspace-card.drop-active {
  outline: 2px dashed var(--accent-1);
  outline-offset: -4px;
}

.drop-overlay {
  position: absolute;
  inset: 0;
  z-index: 5;
  display: grid;
  place-content: center;
  gap: 6px;
  text-align: center;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  opacity: 0.94;
  pointer-events: none;
}

.upload-button.disabled {
  opacity: 0.6;
  cursor: progress;
}

/* YÜKLEME İLERLEMESİ. İnce, tek renkli, kartın genişliğinde bir şerit: bir bakışta oran verir,
   yer kaplamaz. Yalnız yükleme sürerken çizilir. */
.upload-progress {
  height: 4px;
  overflow: hidden;
  border-radius: 999px;
  background: var(--ring-track);
}

.progress-fill {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: var(--accent-1);
  transition: width 0.2s ease;
}

/* Toplu çubukta yıkıcı düğmeyi öbür uca iten esnek boşluk. */
.bulk-spacer {
  flex: 1 1 auto;
  min-width: 12px;
}

.select-cell {
  display: flex;
  align-items: center;
}

.select-cell input {
  width: 16px;
  height: 16px;
  margin: 0;
}

.bulk-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.bulk-bar button {
  width: auto;
}

.transfer-form {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) minmax(180px, 1fr) auto auto;
  gap: 12px;
  align-items: end;
  padding: 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.transfer-form label {
  display: grid;
  gap: 5px;
}

.transfer-form .overwrite-field {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  white-space: normal;
}

.folder-size-result { display: grid; gap: 4px; min-width: 0; }
.folder-size-result button { white-space: normal; text-align: inherit; overflow-wrap: anywhere; }
.size-explanation { color: var(--warn-1); font-size: var(--fs-xs); }

.overwrite-field input {
  flex: 0 0 16px;
  width: 16px;
  height: 16px;
  margin: 2px 0 0;
}

@media (max-width: 900px) {
  .transfer-form {
    grid-template-columns: 1fr;
  }
}

.entry-actions,
.search-row,
.permissions-row {
  display: flex;
  gap: 8px;
  align-items: end;
}

.entry-actions {
  justify-content: flex-end;
  /* `wrap` KALDIRILDI. Satır eylemleri sarmalanabildiği sürece "Sil"in yatay konumu satırdaki
     düğme sayısına göre değişiyordu; sabit iki ögeyle sarmalamaya gerek de kalmadı. */
  flex-wrap: nowrap;
  align-items: center;
}

.entry-actions button,
.entry-actions a,
.search-row button,
.permissions-row button {
  width: auto;
}

/* "…" düğmesi: sabit genişlik, çünkü konumunun satırdan satıra oynamaması tasarımın asıl
   amacı. Üç nokta yalnızca görsel; erişilebilir ad `aria-label` ile ögenin adını taşır. */
.more-toggle {
  flex: 0 0 auto;
  min-width: 32px;
  padding-inline: 8px;
  font-size: 1rem;
  line-height: 1;
  letter-spacing: 0.06em;
}

/* İKİNCİL EYLEM ÇEKMECESİ — satırın altında, ona ait olduğu belli olacak biçimde girintili. */
.actions-drawer {
  background: var(--surface-2);
  box-shadow: inset 2px 0 var(--accent-1);
}

.drawer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding-inline-start: 30px;
}

.drawer-actions button {
  width: auto;
}

.drawer-label {
  color: var(--text-muted);
  font-size: 0.82rem;
  font-weight: 600;
  overflow-wrap: anywhere;
}

/* Yıkıcı eylemi ayıran çizgi: esneyerek "Sil"i çekmecenin öbür ucuna iter ve arada görünür bir
   sınır bırakır. */
.drawer-divider {
  flex: 1 1 auto;
  min-width: 16px;
  height: 1px;
  background: var(--panel-border);
}

/* İNDİR bağlantısı ikincil düğmeyle AYNI görünmeli: ikisi de aynı satırda, aynı işi yapıyor
   izlenimi veriyor. Eski değer `--surface-2` idi ve `button.secondary` `--surface-3` + kenarlık
   kullanıyor; yan yana durduklarında ikisi iki farklı bileşen gibi görünüyordu. */
.action-link {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 30px;
  /* Ölçüler `button.compact` ile BİREBİR aynı belirteçlerden okunur (base.css); sabit piksel
     yazsaydık düğme ölçüleri güncellendiğinde bağlantı yanındaki düğmeden kayardı. */
  padding: var(--sp-4) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-3);
  color: var(--text-strong);
  font-weight: 600;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
}

/* YÜKLEME araç çubuğunun TEK BİRİNCİL eylemidir: bu ekranda kullanıcının en çok yaptığı iş
   dosya koymaktır. Bu yüzden panelin birincil düğme görünümünü (accent gradyanı) alır; yanındaki
   iki oluşturma düğmesi ikincil kalır. Dört eşit düğme arasında göz nereye gideceğini bilmiyordu. */
.upload-button {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 30px;
  padding: var(--sp-4) var(--sp-10);
  border-radius: var(--radius-sm);
  background: linear-gradient(180deg, var(--accent-fill-top), var(--accent-fill-bottom));
  color: var(--on-accent);
  font-weight: 600;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
  box-shadow: 0 1px 1px rgb(0 0 0 / 12%), 0 2px 8px -3px color-mix(in srgb, var(--accent-2) 60%, transparent);
}

/*
 * KLAVYE ERİŞİMİ — ÖLÇÜLMÜŞ BİR HATA DÜZELTİLİYOR.
 *
 * Dosya seçici `display: none` idi. Gizlenen bir öge odak sırasından da çıkar: yani "Dosya yükle"
 * düğmesine KLAVYEYLE HİÇ ULAŞILAMIYORDU. Sürükle-bırakın klavye karşılığı budur; karşılık
 * yoktu, yalnızca var sanılıyordu.
 *
 * Doğru kalıp, ögeyi görünmez yapıp akışta bırakmaktır (base.css `.sr-only` ile aynı teknik).
 * Odak halkası etikette çizilir: odaklanan öge görünmez olduğu için tarayıcının kendi halkası
 * hiçbir yerde belirmezdi.
 */
.upload-button input {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.upload-button:focus-within {
  outline: 2px solid var(--focus-ring);
  outline-offset: 2px;
}

.file-search { display: grid; gap: var(--sp-8); min-width: 0; }
.search-row { flex-wrap: wrap; min-width: 0; }
.search-field { flex: 1 1 220px; min-width: 0; display: grid; gap: 6px; }
.search-field span { font-size: var(--fs-xs); color: var(--text-muted); }
.search-context { overflow-wrap: anywhere; margin: 0; }
.search-panel { display: grid; gap: var(--sp-8); padding: var(--sp-12); border: 1px solid var(--panel-border); border-radius: var(--radius-sm); min-width: 0; background: var(--surface-1); }
.search-heading { display: flex; flex-wrap: wrap; gap: var(--sp-8); align-items: center; justify-content: space-between; }
.search-heading h3, .folder-list-heading { margin: 0; font-size: var(--fs-sm); }
.search-panel .meta, .search-panel .empty-state { margin: 0; }
.search-warning { margin: 0; color: var(--warn-1); font-size: var(--fs-xs); overflow-wrap: anywhere; }

.search-results {
  display: grid;
  gap: 6px;
  min-width: 0;
}

.search-result {
  display: grid;
  gap: 3px;
  background: var(--surface-2);
  color: var(--text-strong);
  text-align: left;
  min-width: 0;
  white-space: normal;
  overflow-wrap: anywhere;
}

.search-result small {
  color: var(--text-muted);
}

/* İzinler kartın ALT şeridi: üstündeki ayırıcı çizgi, buranın günlük dosya işlerinden ayrı bir
   yönetim alanı olduğunu söyler. */
.permissions-section {
  padding-top: 12px;
  border-top: 1px solid var(--panel-border);
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
}
.permissions-section h3 { margin: 0 0 var(--sp-8); }
.permissions-section .field-hint { margin: var(--sp-8) 0 0; }
.permission-target { display: grid; gap: var(--sp-4); margin: 0; }
.permission-target span { color: var(--text-muted); font-size: var(--fs-xs); }
.permission-target strong { overflow-wrap: anywhere; font-weight: 600; }
.permissions-row {
  flex-wrap: wrap;
}

.permissions-row label {
  display: grid;
  gap: 5px;
  flex: 1;
  min-width: min(180px, 100%);
}

/* Hizalamanın anlam taşıdığı dosyalar (.htaccess, web.config, robots.txt) orantılı yazı tipinde
   okunmuyordu; girinti ve sütun hizası ancak tek aralıklı yazı tipinde görünür. */
.file-editor {
  font: 0.86rem/1.55 "Cascadia Code", "Consolas", monospace;
  tab-size: 4;
}

/*
 * BAĞLANTI GİBİ DÜĞME — global `button` kuralının MİRASI KESİLİYOR.
 *
 * `background: transparent` yalnız gradyanı siliyordu; `button` kuralının gölgesi, en az yükseklik
 * değeri ve `:hover` üzerindeki `translateY(-1px)` + `brightness()` etkileri duruyordu. Sonuç:
 * yol çubuğundaki her parça ve tablodaki her dosya adı, üzerine gelindiğinde bir piksel ZIPLIYOR
 * ve altında accent renginde bir gölge taşıyordu. Bağlantı zıplamaz; altı çizilir.
 */
.link-button {
  min-height: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--accent-1);
  font-weight: inherit;
  text-align: left;
  box-shadow: none;
}

.link-button:not(:disabled):hover {
  background: transparent;
  box-shadow: none;
  transform: none;
  filter: none;
  text-decoration: underline;
}

.meta {
  margin: 0;
  color: var(--text-muted);
}

/*
 * BİLDİRİM ŞERİDİ base.css'te `position: fixed` (sağ alt köşe). Burada yalnız İÇ düzeni
 * veriliyor: metin ile kapatma düğmesi yan yana, düğme metnin üstüne binmesin.
 */
.page-notice {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.notice-text {
  flex: 1;
  min-width: 0;
}

.notice-dismiss {
  flex: 0 0 auto;
  min-height: 0;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: inherit;
  font-size: 1rem;
  line-height: 1;
  opacity: 0.7;
  box-shadow: none;
}

.notice-dismiss:not(:disabled):hover {
  background: color-mix(in srgb, currentcolor 12%, transparent);
  opacity: 1;
  transform: none;
  filter: none;
  box-shadow: none;
}

/* Keep file actions visible when the workspace is narrower than the six-column table. */
@container file-workspace (max-width: 790px) {
  .files-table .data-row { min-width: 0; }
  .files-table .table-head { grid-template-columns: minmax(0, 1fr); }
  .files-table .table-head > :not(.select-cell) {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }
  .files-table .table-head .select-cell { display: flex; gap: 10px; align-items: center; }
  .mobile-select-label { display: inline; font-weight: 500; font-size: var(--fs-xs); }
  .files-row:not(.table-head) {
    grid-template-columns: 22px minmax(0, 1fr) minmax(0, 1fr);
    grid-template-areas: "select name name" ". kind size" ". date date" ". actions actions";
    gap: 8px 10px;
    padding-block: 14px;
  }
  .files-row:not(.table-head) .select-cell { grid-area: select; align-self: start; }
  .files-row .cell-main { grid-area: name; }
  .files-row .kind-cell { grid-area: kind; }
  .files-row:not(.table-head) .size-cell { grid-area: size; text-align: left; white-space: normal; }
  .files-row:not(.table-head) .date-cell { grid-area: date; white-space: normal; }
  .files-row .entry-actions { grid-area: actions; justify-content: flex-end; }
  .files-row [data-label] { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; font-size: var(--fs-xs); color: var(--text-muted); }
  .files-row [data-label]::before { content: attr(data-label); font-weight: 600; }
  .files-row .entry-name { overflow-wrap: anywhere; }
  .files-row .size-calc { text-align: left; font-size: inherit; }
  .files-table .actions-drawer { grid-template-columns: minmax(0, 1fr); }
  .drawer-actions { padding-inline-start: 0; gap: 10px; }
  .drawer-label { flex-basis: 100%; }
  .drawer-actions button { flex: 1 1 110px; }
  .drawer-divider { flex-basis: 100%; }
  .breadcrumb { max-width: 100%; }
  .action-row > .toolbar-group { min-width: 0; max-width: 100%; }
  .transfer-form { grid-template-columns: minmax(0, 1fr); }
  .transfer-form label { min-width: 0; }
}

@media (max-width: 900px) {
  .backup-row,
  .form-grid {
    grid-template-columns: 1fr;
  }

  .action-row {
    align-items: flex-start;
  }
}
.editor-actions { position: sticky; bottom: 0; z-index: 2; padding: 12px 0; background: var(--surface-1); border-top: 1px solid var(--panel-border); flex-wrap: wrap; }
</style>
