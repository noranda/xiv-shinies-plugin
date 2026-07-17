// Posts a published GitHub Release to the Discord #releases channel as a rich embed,
// attaching that release's screenshots when the repo carries any.
//
// Runs from the release job in .github/workflows/release.yml with the release's details
// in environment variables (never interpolated into a shell line — a release body is
// arbitrary text).
// Deliberately dependency-free: Node 20+ ships fetch/FormData/Blob, so there is nothing
// to install and nothing to audit beyond this file.
//
// Set DRY_RUN=1 to print the payload and the screenshot list instead of posting —
// useful for verifying locally before trusting CI with it.

import { readdir, readFile } from "node:fs/promises";
import path from "node:path";

// Discord caps an embed description at 4096 characters; trimming a little below that
// leaves room for the truncation notice itself.
const DESCRIPTION_LIMIT = 4000;

// Discord accepts at most 10 attachments per message.
const MAX_ATTACHMENTS = 10;

// The image types Discord renders inline; anything else in the folder is skipped.
const IMAGE_EXTENSIONS = new Set([".png", ".jpg", ".jpeg", ".gif", ".webp"]);

// Brand.Gold (see src/XIVShinies.SyncPlugin/Windows/Brand.cs) as a Discord color int —
// deliberately distinct from the web app's teal 0x12a597 so the two products' release
// posts are tellable apart at a glance in the shared channel.
const EMBED_COLOR = 0xfac424;

// The plugin's user-facing name and icon, shown as the webhook's author identity. The
// avatar must be a public URL, so it points at the icon on raw.githubusercontent.com.
const USERNAME = "XIV Shinies Sync";
const AVATAR_URL =
  "https://raw.githubusercontent.com/noranda/xiv-shinies-plugin/main/src/XIVShinies.SyncPlugin/images/icon.png";

// Reads one required value out of the environment, failing loudly rather than posting
// a half-empty embed. The webhook URL is checked separately so DRY_RUN works without it.
function requireEnv(name) {
  const value = process.env[name];
  if (!value) {
    console.error(`Missing required environment variable: ${name}`);
    process.exit(1);
  }
  return value;
}

// The release body, cut to Discord's limit when a changelog section runs long. The
// notice replaces the tail rather than being appended past the limit.
function truncate(body) {
  if (body.length <= DESCRIPTION_LIMIT) return body;
  const notice = "\n\n… truncated — full notes on the release page.";
  return body.slice(0, DESCRIPTION_LIMIT - notice.length) + notice;
}

// Collects this release's screenshots: every image file in images/releases/<tag>/,
// name-sorted so the posting order is predictable. A missing folder simply means a
// text-only announcement — that is a normal release, not an error.
async function collectScreenshots(tag) {
  const folder = path.join("images", "releases", tag);

  let entries;
  try {
    entries = await readdir(folder, { withFileTypes: true });
  } catch {
    console.log(`No screenshot folder at ${folder}; posting text-only.`);
    return [];
  }

  // Numeric-aware sort, so a "10-" prefix lands after "9-" instead of between "1-" and
  // "2-" the way plain lexicographic ordering would put it.
  const images = entries
    .filter((entry) => entry.isFile() && IMAGE_EXTENSIONS.has(path.extname(entry.name).toLowerCase()))
    .map((entry) => path.join(folder, entry.name))
    .sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));

  if (images.length > MAX_ATTACHMENTS) {
    console.warn(
      `${images.length} screenshots in ${folder}; Discord allows ${MAX_ATTACHMENTS} — attaching the first ${MAX_ATTACHMENTS}.`,
    );
    return images.slice(0, MAX_ATTACHMENTS);
  }

  return images;
}

const url = requireEnv("RELEASE_URL");
const tag = requireEnv("RELEASE_TAG");

// A release published without an explicit title reaches the workflow with a null name.
// The tag is always present and names the release well enough, so it stands in rather
// than dropping the announcement over a cosmetic field.
const name = process.env.RELEASE_NAME || tag;

// An empty body is tolerated (the embed still links to the release); an unset variable
// would more likely mean the workflow wiring broke, but the two are indistinguishable
// here, so the post goes out either way rather than silently skipping a release.
const body = process.env.RELEASE_BODY ?? "";

// The embed's timestamp is the release's own publish time when the workflow provides it;
// the moment this script happens to run is only a fallback. Discord rejects the whole
// embed unless the timestamp is ISO 8601, and the value's spelling depends on the calling
// shell — PowerShell 7's ConvertFrom-Json turns ISO date strings into DateTime objects,
// which stringify into a culture-formatted date on the way into the environment — so
// whatever arrives is parsed and re-emitted in the one form Discord accepts.
const publishedAtMs = Date.parse(process.env.RELEASE_PUBLISHED_AT ?? "");
const timestamp = Number.isNaN(publishedAtMs)
    ? new Date().toISOString()
    : new Date(publishedAtMs).toISOString();

const payload = {
  username: USERNAME,
  avatar_url: AVATAR_URL,
  embeds: [
    {
      title: name,
      url,
      description: truncate(body),
      color: EMBED_COLOR,
      timestamp,
    },
  ],
};

const screenshots = await collectScreenshots(tag);

if (process.env.DRY_RUN === "1") {
  console.log("DRY_RUN — would post this payload:");
  console.log(JSON.stringify(payload, null, 2));
  console.log(`Screenshots (${screenshots.length}):`);
  for (const file of screenshots) console.log(`  ${file}`);
  process.exit(0);
}

const webhook = requireEnv("DISCORD_RELEASES_WEBHOOK_URL");

// Attachments ride a multipart form beside the JSON payload, each under the files[n]
// field name Discord's webhook API expects. With no screenshots, plain JSON suffices.
let response;
if (screenshots.length > 0) {
  const form = new FormData();
  form.append("payload_json", JSON.stringify(payload));
  for (const [index, file] of screenshots.entries()) {
    form.append(`files[${index}]`, new Blob([await readFile(file)]), path.basename(file));
  }
  response = await fetch(`${webhook}?wait=true`, { method: "POST", body: form });
} else {
  response = await fetch(`${webhook}?wait=true`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
}

// ?wait=true makes Discord return the created message instead of a 204, so success is
// verifiable (and the message id lands in the workflow log for later reference).
if (!response.ok) {
  console.error(`Discord webhook responded ${response.status}: ${await response.text()}`);
  process.exit(1);
}

const message = await response.json();
console.log(`Posted release announcement: message id ${message.id}`);
