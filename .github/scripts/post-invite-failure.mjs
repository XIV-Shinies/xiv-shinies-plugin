// Tells the admin Discord channel that the invite check failed, with a link to the run.
//
// Runs from the Discord invite workflow (.github/workflows/invite.yml) after a failed step. The
// webhook URL is a secret, so it is never printed. Set DRY_RUN=1 to print the message instead.

// REASON is empty when the run failed before the check could say why.
const reason = process.env.REASON ?? "";

const runUrl = process.env.RUN_URL ?? "";

// Returns the exit code (see check-discord-invite.mjs for why it is not passed to process.exit).
async function postFailure() {
  const content = ["⚠️ The Discord invite check failed.", reason, runUrl].filter(Boolean).join("\n");

  if (process.env.DRY_RUN === "1") {
    console.log("DRY_RUN — would post this message:");
    console.log(content);
    return 0;
  }

  // Without the secret, GitHub's own failure notice is the only alert, and the run says so.
  const webhook = process.env.DISCORD_ADMIN_WEBHOOK_URL;
  if (!webhook) {
    console.log("DISCORD_ADMIN_WEBHOOK_URL is not set, so the admin channel is not told.");
    return 0;
  }

  const response = await fetch(webhook, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ content }),
  });

  if (!response.ok) {
    console.error(`Discord's webhook answered ${response.status}: ${await response.text()}`);
    return 1;
  }

  console.log("Told the admin channel.");
  return 0;
}

process.exitCode = await postFailure();
