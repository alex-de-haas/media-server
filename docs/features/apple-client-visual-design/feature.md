# Apple Client Visual Design and Collections

Created: 2026-09-06
Updated: 2026-09-08

Collections reload through the store’s screen-appearance handler whenever the
collection screen appears. The unsupported-server
message offers a retry so a server upgrade can be detected without restarting
the client. Overlapping collection refresh attempts are ignored while a request
is in flight; subsequent refreshes remain available after success or failure.

## Presentation

The tvOS client follows the system light/dark appearance with an adaptive neutral canvas, system typography, and native card
focus. Its top-level tabs are Home, Movies, Series, Collections, and Settings.

[Home](../apple-tv-home/feature.md) owns Continue Watching, Next Up, and held
recommendations. Movies and Series retain complete poster grids. Home refreshes
per-user rails independently on appearance; detail reads still update the library's
local watched/resume state. Episode resume cards navigate to their owning series.
Poster/caption groups and shelves participate in directional focus navigation;
the captions remain outside the visual card. The transition respects Reduce Motion.

Movie and series cards show only one compact metadata line below the poster:
year and available HDR/Dolby Vision formats in the library. Home cards retain
visible titles and resume, episode, or recommendation captions. Formats aggregate the movie's sources without Dolby Vision
profiles; they describe available files, not device playback support. The server
projects these labels once per page in `videoFormats`, without detail requests
per card. Older servers omit the field and the client displays the year alone. Titles are not
repeated above shelves or grids when a card receives focus.
The poster and metadata scale together on focus, with a shadow confined to the
poster and a short gap between artwork and metadata. Missing or failed artwork
shows the title centered inside a neutral poster. VoiceOver retains the title,
metadata, and watched/resume status. Collection cards retain their names.
Artwork loads through the authenticated server loader, retaining the same
geometry when missing or still loading. Collection member cards reuse the
library's current state when available, using a single ID lookup built per grid
update. The collection artwork endpoint advertises JPEG, PNG, WebP, and GIF
response formats in the native OpenAPI contract.

Title and collection details preserve the server backdrop's color with localized
black gradients under the left-hand text and along the bottom, without a white
wash or blur. Detail screens with backdrop artwork use white text and dark
controls in either system appearance; library and settings retain the system
theme. Increased contrast strengthens the dark overlay. Details without a
backdrop use the system-themed neutral background. The title, facts, Play/Resume, and full synopsis lead. The synopsis is ordinary
text with no line limit, focus highlight, button, or sheet. Long descriptions
scroll with the detail screen. All sources
appear as selectable inline rows with a checkmark identifying the playback choice.
Each row shows the edition name (or Original when unnamed), container, video
codec, and file size on the left. Available dynamic-range badges (including the
Dolby Vision profile) and fallback notices sit on the right. The HDR10 fallback
notice includes profile 8 with compatibility ID 6 even without an enhancement
layer, matching the server's existing playback signalling.
The selected source's file, audio, and subtitle details open in a separate sheet.
The sheet has an explicit 1280 × 820-point frame so its flexible list cannot
collapse to the header height. A native list with visibly highlighted focusable
rows lets the remote reach every track, including
rows below the viewport; empty sections show None. The remote's Back button dismisses it; sheets have no separate Close button. The player, capability negotiation,
sidecar handling, and resume reporting retain their existing behavior.

Title details show Crew in the same horizontal portrait cards as Cast, with
production jobs beneath names. The optional API `crew` list reads stored person
credits, including identity, job, department, and portrait URL. Name-only director
and creator credits remain as fallback cards on older servers or when not present
in the stored crew list. Cast appears in a horizontal row of portrait cards with names and
character roles, retaining server billing order and neutral image fallbacks.
Names use their actual line count, so the role follows directly without reserving
an empty second name line. Public portrait URLs use a separate loader
without server credentials. Credit cards accept visible focus so the remote can
scroll horizontally through the cast; they do not open a person detail page.

Settings separates playback from expandable capabilities and diagnostics.
Sign out uses a neutral bordered button with an exit symbol and adaptive system
text colors, keeping the label legible both with and without focus in either theme.
Existing preferences retain their storage. Pairing displays a locally generated
QR code for an HTTP(S) verification URI alongside the human-readable code;
missing URIs retain manual Hosty instructions. The QR code contains only the
server-provided verification URI.

## Collections

Collections uses the existing TMDb movie franchise model and the owned-movie
threshold of two. It spans catalogs. The tab remains visible with an empty
state when no franchise qualifies. Members open the ordinary title screen and
retain release order, with unknown years last and stable ID tie-breaking.

The native surface provides authenticated reads:

- `GET /native/v1/collections` — names, IDs, movie counts, and native poster URLs.
- `GET /native/v1/collections/{id}` — ordered movie cards and native artwork URLs;
  unknown collections and collections below the threshold return 404.
- `GET /native/v1/collections/{id}/images/{imageType}` — cached franchise artwork,
  with a member-poster fallback for Primary. Responses carry an ETag.

The routes reuse the collection read model and image cache. Removed and
unpublished movies cannot contribute to eligibility, members, or fallback art.
Collection image URLs carry source-derived cache tags; missing artwork is null.
The generated Swift client is regenerated from the committed native OpenAPI.

MediaKit distinguishes a list-route 404 (server update required), a successful
empty list, and a network/server failure. A detail 404 returns to a refreshed
list. Failures provide retry controls. No provider CDN URL is fetched by the
client for collection artwork.

## Development preview

Debug builds support `--cinema-preview`, which opens the real views with a
local `ClientTransport` fixture. It uses no stored credentials and connects to
no server. The additional `--preview-light` argument renders the local fixture in light
appearance; ordinary launches follow the system. Release builds do not include
this mode. SwiftUI previews cover the
library's long titles and missing artwork. Remaining integration and device
acceptance checks are tracked in [the plan](plan.md).

## Testing Expectations

- MediaKit tests cover collection decoding, bearer use, empty/unsupported/error
  distinctions, detail removal, continue-row filtering and completion refresh,
  and resume timestamp formatting, alongside existing pairing/playback tests.
- Backend xUnit tests cover native URL projection, cache-tag changes, member
  identity/order, route authorization/public metadata, and removal filtering.
  Shared collection and Jellyfin tests guard existing behavior.
- Build tvOS with Xcode and exercise focus, back navigation, tab switching,
  full synopsis layout and technical-detail sheet navigation, and visual fallbacks in the simulator.
- Validate real artwork, pairing QR scanning, playback, Siri Remote focus, and
  accessibility on Apple TV against a Core-managed instance before acceptance.
