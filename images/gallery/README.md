# Installer gallery images

These are the screenshots the **in-game plugin installer** shows, listed by `ImageUrls` in
[`repo.json`](../../repo.json). They are resized copies of the full-size originals in
[`../screenshots/`](../screenshots), which the project README embeds instead.

## The size limit is a hard reject, not a downscale

Dalamud refuses any gallery image larger than **730×380** and logs an error like:

```
[ERR] Plugin image1 for XIVShinies.SyncPlugin at queue was larger than the
      maximum allowed resolution (1694x907 > 730x380).
```

A rejected image is simply absent — the gallery renders with fewer images, or none at all,
and nothing in the installer says why. The failure is silent from the user's side, so it is
easy to ship and never notice.

## Adding or replacing a screenshot

1. Put the full-size capture in `../screenshots/` (that copy stays full size — the README
   embeds several of them and shrinking them there would degrade the front page).
2. Add a copy here, scaled to fit **inside 730×380** with its aspect ratio preserved. Scale
   by the smaller of `730 / width` and `380 / height`; both dimensions must land at or under
   the cap.
3. If the image should appear in the installer, add its raw URL to `ImageUrls` — in **both**
   `repo.json` and `src/XIVShinies.SyncPlugin/XIVShinies.SyncPlugin.json`, which must agree.
   Dalamud accepts at most **five**; once the list is full, adding one means dropping one.
4. Verify after merging: the URLs are served from `main`, not from a release tag, so the
   gallery updates as soon as the images land there — no version bump, no release needed.
   Check `%AppData%\XIVLauncher\dalamud.log` for the resolution error above.

Tall screenshots pay the most: a portrait capture fits the 380px height long before it
approaches the 730px width, so it lands near 380px wide and reads small in the gallery. Prefer
captures that are wider than they are tall for anything meant to be legible here — which is
why the gallery carries the landscape settings views, while the taller wizard captures earn
their place in the project README, where they display at full size.
