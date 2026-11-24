import { expect, it } from "vitest";
import { ftpTrustTarget } from "./backupNetworkTrust";

it("sends only an exact normalized FTP origin", () => {
  expect(ftpTrustTarget("ftp://NAS.example./private/path")).toBe("ftp://nas.example:21/");
  expect(ftpTrustTarget("ftp://nas.example:2121/path")).toBe("ftp://nas.example:2121/");
  expect(ftpTrustTarget("ftp://bücher.example/path")).toBe("ftp://xn--bcher-kva.example:21/");
  for (const url of ["https://nas.example/", "ftp://user:SECRET@nas.example/", "ftp://nas.example/?SECRET", "ftp://nas.example/#SECRET"])
    expect(ftpTrustTarget(url)).toBeNull();
});
