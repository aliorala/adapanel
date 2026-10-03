import { UserFacingError } from "./apiError";
import { toReachableUrl } from "./url";

/** Tıklama sırasında sekmeyi ayır; adresi üreten istek daha sonra tamamlanabilir. */
export async function openAsyncReachable(resolveUrl: () => Promise<string>): Promise<void> {
  const tab = window.open("", "_blank");
  if (!tab) throw new UserFacingError("panel.common.popupBlocked");
  try {
    // noopener özelliği window.open dönüşünü null yapar; pencere tutamacını aldıktan
    // sonra ayır ki hedef sayfa paneli yönlendiremesin.
    tab.opener = null;
    const url = toReachableUrl(await resolveUrl());
    if (!url) throw new UserFacingError("panel.common.invalidLink");
    if (tab.closed) throw new UserFacingError("panel.common.popupClosed");
    tab.location.replace(url);
  } catch (error) {
    tab.close();
    throw error;
  }
}
