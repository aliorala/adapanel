/**
 * Sürüm notu ayrıştırıcısı.
 *
 * Bilerek SUNUMDAN AYRI: bu dosya yalnızca metni bloklara çevirir, hiç DOM üretmez.
 * Böylece ayrıştırma mantığı bir tarayıcı olmadan, gerçek sürüm notlarıyla ölçülebilir
 * (bkz. scripts/check-release-notes-parser.mjs). Çizim ReleaseNotes.vue'da yapılır ve
 * orada metin h() ile düğüme çevrildiği için HTML asla yorumlanmaz.
 */

export type Parca = { tur: "metin" | "kalin" | "kod"; deger: string };

/** Şablonda dar tip daralması aramamak için bütün bloklar aynı biçimi taşır. */
export interface Blok {
  tur: "baslik" | "paragraf" | "kod" | "liste" | "alinti" | "ayrac";
  seviye: number;
  parcalar: Parca[];
  ogeler: Parca[][];
  icerik: string;
}

function blok(tur: Blok["tur"], ek: Partial<Blok> = {}): Blok {
  return { tur, seviye: 1, parcalar: [], ogeler: [], icerik: "", ...ek };
}

/** Satır içi: **kalın** ve `kod`. Kapanmayan işaret düz metin olarak kalır. */
export function parcala(satir: string): Parca[] {
  const parcalar: Parca[] = [];
  const desen = /\*\*([^*]+)\*\*|`([^`]+)`/g;
  let son = 0;
  let eslesme: RegExpExecArray | null;

  while ((eslesme = desen.exec(satir)) !== null) {
    if (eslesme.index > son) {
      parcalar.push({ tur: "metin", deger: satir.slice(son, eslesme.index) });
    }
    if (eslesme[1] !== undefined) {
      parcalar.push({ tur: "kalin", deger: eslesme[1] });
    } else {
      parcalar.push({ tur: "kod", deger: eslesme[2] });
    }
    son = eslesme.index + eslesme[0].length;
  }
  if (son < satir.length) {
    parcalar.push({ tur: "metin", deger: satir.slice(son) });
  }
  return parcalar;
}

const KOD_CITI = /^\s*```/;
const AYRAC = /^\s*(-{3,}|\*{3,}|_{3,})\s*$/;
const BASLIK = /^(#{1,6})\s+(.*)$/;
const ALINTI = /^\s*>\s?/;
const MADDE = /^\s*([-*]|\d+\.)\s+/;

/** Paragraf birikimi bu satırlarda DURMALI; yoksa başlık/liste paragrafa yapışır. */
function ozelSatir(satir: string): boolean {
  return (
    KOD_CITI.test(satir) ||
    AYRAC.test(satir) ||
    BASLIK.test(satir) ||
    ALINTI.test(satir) ||
    MADDE.test(satir)
  );
}

export function ayristir(metin: string): Blok[] {
  const bloklar: Blok[] = [];
  const satirlar = (metin ?? "").replace(/\r\n/g, "\n").split("\n");
  let i = 0;

  while (i < satirlar.length) {
    const satir = satirlar[i];

    if (satir.trim() === "") {
      i++;
      continue;
    }

    // Kod bloğu: içerik AYRIŞTIRILMAZ, olduğu gibi korunur.
    if (KOD_CITI.test(satir)) {
      const govde: string[] = [];
      i++;
      while (i < satirlar.length && !KOD_CITI.test(satirlar[i])) {
        govde.push(satirlar[i]);
        i++;
      }
      i++; // kapanış çiti; metin çit kapanmadan biterse döngü zaten sonlanır
      bloklar.push(blok("kod", { icerik: govde.join("\n") }));
      continue;
    }

    if (AYRAC.test(satir)) {
      bloklar.push(blok("ayrac"));
      i++;
      continue;
    }

    const baslik = BASLIK.exec(satir);
    if (baslik) {
      bloklar.push(blok("baslik", { seviye: baslik[1].length, parcalar: parcala(baslik[2]) }));
      i++;
      continue;
    }

    if (ALINTI.test(satir)) {
      const govde: string[] = [];
      while (i < satirlar.length && ALINTI.test(satirlar[i])) {
        govde.push(satirlar[i].replace(ALINTI, ""));
        i++;
      }
      bloklar.push(blok("alinti", { parcalar: parcala(govde.join(" ")) }));
      continue;
    }

    if (MADDE.test(satir)) {
      const ogeler: Parca[][] = [];
      while (i < satirlar.length && MADDE.test(satirlar[i])) {
        ogeler.push(parcala(satirlar[i].replace(MADDE, "")));
        i++;
      }
      bloklar.push(blok("liste", { ogeler }));
      continue;
    }

    const govde: string[] = [];
    while (i < satirlar.length && satirlar[i].trim() !== "" && !ozelSatir(satirlar[i])) {
      govde.push(satirlar[i].trim());
      i++;
    }
    bloklar.push(blok("paragraf", { parcalar: parcala(govde.join(" ")) }));
  }

  return bloklar;
}
