// Fails when the Discord invite the plugin opens no longer works. The invite page answers 200
// even for a dead invite, so this asks Discord's invite API, which answers 404 for one.
//
// Runs from the Discord invite workflow (.github/workflows/invite.yml), at the repository root.
// Dependency-free: Node 20+ ships fetch.

import { appendFile, readFile } from "node:fs/promises";

// PluginMeta.DiscordUrl is the invite's source; ReadmeTests keeps the README's copy equal to it.
const SOURCE = "src/XIVShinies.SyncPlugin/PluginMeta.cs";

// A rate limit or server error says nothing about the invite, so those are retried before failing.
const ATTEMPTS = 3;
const RETRY_DELAY_MS = 30_000;

// Prints why the check failed and, on GitHub, hands that reason to the step that tells the admin
// channel. No line may hold a line break, because a step output ends at the first one.
async function fail(...lines) {
  for (const line of lines) console.error(line);
  if (process.env.GITHUB_OUTPUT) {
    await appendFile(process.env.GITHUB_OUTPUT, `reason=${lines.join(" ")}\n`);
  }
  return 1;
}

// Returns the exit code. The caller sets process.exitCode rather than calling process.exit, which
// can crash Node on Windows while fetch still holds a connection open.
async function checkInvite() {
  const source = await readFile(SOURCE, "utf8");
  const match = source.match(/DiscordUrl = "https:\/\/discord\.gg\/([A-Za-z0-9-]+)"/);

  // Fails rather than passes, so a renamed or reshaped constant cannot leave the invite unchecked.
  if (!match) {
    return fail(`No discord.gg invite found in PluginMeta.DiscordUrl (${SOURCE}).`);
  }

  const code = match[1];
  const invite = `https://discord.gg/${code}`;

  let response;
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    response = await fetch(`https://discord.com/api/v10/invites/${code}`);
    if (response.status !== 429 && response.status < 500) break;
    if (attempt < ATTEMPTS) await new Promise((resolve) => setTimeout(resolve, RETRY_DELAY_MS));
  }

  if (response.status === 404) {
    return fail(
      `The Discord invite ${invite} is dead. Make one that never expires, then update`,
      "PluginMeta.DiscordUrl, the README and the website to it.",
    );
  }

  // The body goes to the log only: it can span lines, which a step output cannot carry.
  if (!response.ok) {
    console.error(await response.text());
    return fail(`Discord's invite API answered ${response.status} for ${invite}.`);
  }

  const details = await response.json();

  // An invite with an expiry still opens today but dies on that date.
  if (details.expires_at) {
    console.log(`::warning::The Discord invite ${invite} expires at ${details.expires_at}.`);
  }

  console.log(`The Discord invite ${invite} opens ${details.guild?.name ?? "a server"}.`);
  return 0;
}

process.exitCode = await checkInvite();
