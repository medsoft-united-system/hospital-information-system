#!/usr/bin/env python3
import argparse
import http.cookiejar
import json
from html.parser import HTMLParser
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request


class Form(HTMLParser):
    def __init__(self):
        super().__init__()
        self.values = {}

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "input" and attrs.get("name") in ("csrf", "version"):
            self.values[attrs["name"]] = attrs.get("value", "")


class LocalRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, origin):
        self.origin = origin

    def redirect_request(self, request, response, code, message, headers, target):
        parsed = urllib.parse.urlsplit(target)
        if parsed.scheme + "://" + parsed.netloc != self.origin or parsed.path != "/connection":
            return None
        return super().redirect_request(request, response, code, message, headers, target)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--lis-url", required=True)
    parser.add_argument("--his-origin", required=True)
    parser.add_argument("--key-file", required=True)
    parser.add_argument("--identity", choices=("new", "same"), default="new")
    args = parser.parse_args()
    path = Path(args.key_file)
    if path.is_symlink() or not path.is_file() or path.stat().st_mode & 0o077:
        raise ValueError("key file must be regular with mode 0600")
    key = path.read_text().rstrip("\r\n")
    if not key or any(c in key for c in "\r\n\0"):
        raise ValueError("invalid key file")
    base = args.lis_url.rstrip("/")
    parsed = urllib.parse.urlsplit(base)
    if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost") or parsed.path:
        raise ValueError("LIS smoke must use loopback HTTP endpoint")
    jar = http.cookiejar.CookieJar()
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), LocalRedirect(base),
                                         urllib.request.HTTPCookieProcessor(jar))

    def request(path, values=None, cross_site=False):
        headers = {"Origin": "https://cross-site.invalid" if cross_site else base,
                   "Sec-Fetch-Site": "cross-site" if cross_site else "same-origin"}
        data = None
        if values is not None:
            headers["Content-Type"] = "application/x-www-form-urlencoded"
            data = urllib.parse.urlencode(values).encode()
        with client.open(urllib.request.Request(base + path, data=data, headers=headers), timeout=55) as response:
            body = response.read(1024 * 1024).decode("utf-8")
            if key in body:
                raise ValueError("LIS reflected API key")
            form = Form()
            form.feed(body)
            return body, form.values

    _, form = request("/connection")
    if len(form.get("csrf", "")) != 64:
        raise ValueError("LIS did not return protected connection form")
    _, form = request("/connection", form | {"origin": args.his_origin, "apiKey": key, "identity": args.identity})
    if int(form.get("version", "0")) < 1:
        raise ValueError("LIS did not persist connection settings")
    body, form = request("/connection/check", form)
    if "Проверка HIS и FHIR R5 успешна на момент запроса" not in body:
        raise ValueError("actual LIS connection check did not confirm HIS + FHIR R5")
    try:
        request("/connection/check", form, cross_site=True)
    except urllib.error.HTTPError as error:
        if error.code != 403:
            raise ValueError("unexpected cross-origin rejection status") from None
    else:
        raise ValueError("LIS accepted cross-origin credential action")
    print(json.dumps({"stage": "actual_lis_connection_form", "status": "passed",
                      "csrf": "passed", "identity": args.identity, "secretsReflected": False}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"status": "failed", "errorType": type(error).__name__, "message": str(error)}))
        raise SystemExit(1)
