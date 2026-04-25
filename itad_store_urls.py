#!/usr/bin/env python3
"""
Given a Steam app ID, output all store URLs that IsThereAnyDeal knows about.
Usage: python itad_store_urls.py <steam_app_id>
"""

import sys
import requests

API_KEY = "908ac5230ba0d23d28db3907fc18ab9121def642"
BASE = "https://api.isthereanydeal.com"
SESSION = requests.Session()
SESSION.params = {"key": API_KEY}


def itad_get(path, params=None):
    r = SESSION.get(f"{BASE}{path}", params=params or {})
    r.raise_for_status()
    return r.json()


def itad_post(path, params=None, body=None):
    r = SESSION.post(f"{BASE}{path}", params=params or {}, json=body)
    r.raise_for_status()
    return r.json()


def main():
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <steam_app_id>")
        sys.exit(1)

    steam_id = sys.argv[1]

    # Step 1: resolve Steam app ID → ITAD game ID
    lookup = itad_get("/games/lookup/v1", {"appid": steam_id})
    if not lookup.get("found"):
        print(f"Game not found on ITAD for Steam app {steam_id}")
        sys.exit(1)

    game = lookup["game"]
    game_id = game["id"]
    print(f"Title  : {game['title']}")
    print(f"ITAD ID: {game_id}")
    print(f"Slug   : {game['slug']}\n")

    # Step 2: POST list of IDs to get prices + store URLs
    results = itad_post("/games/prices/v3", {"country": "US"}, [game_id])

    if not results:
        print("No store data returned.")
        return

    deals = results[0].get("deals", [])
    if not deals:
        print("No deals found.")
        return

    print(f"{'Shop':<24} {'Price':>8}  {'Cut':>4}  {'DRM':<20}  URL")
    print("-" * 100)
    for deal in sorted(deals, key=lambda d: d["price"]["amount"]):
        shop = deal["shop"]["name"]
        amount = deal["price"]["amount"]
        currency = deal["price"]["currency"]
        cut = deal.get("cut", 0)
        drm = ", ".join(d["name"] for d in deal.get("drm", [])) or "—"
        url = deal.get("url", "")
        print(f"{shop:<24} {currency} {amount:>6.2f}  {cut:>3}%  {drm:<20}  {url}")


if __name__ == "__main__":
    main()
