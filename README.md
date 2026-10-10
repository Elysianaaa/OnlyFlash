# OnlyFlash

A mod for **Secret Flasher Manaka** that gives Manaka her own secret fan page.

Sneak pics while you're out, post them, and watch AI-written fans react. Every comment and DM is written live by an AI that actually reacts to what's in your photo. Fans have their own personalities and remember your past chats and posts, and they text you first: reacting to your new post, flirting, daring you to do something where you are right now, or asking what you're wearing. You can text back, send them private pics, or invite them over, and some will literally come find you in person. You can even create your own star fans: give them a name, personality, texting style and look, and they'll join your page, text you in character and remember everything.

Public posts pull in likes, comments and new followers. The bolder the pic (and the more you've posted), the further it spreads. Followers turn into paying subscribers across 3 tiers you set the price for. Bolder posts in a tier keep more people paying, and every subscriber pays you RP when you make it home safe. A local vision AI looks at every pic and judges how daring it really is, so going riskier (daylight, people nearby) is how your page blows up.

## How it plays
- **B** = freecam camera (tap = time keeps going, hold = time stop, F1 = hide tips). Pick square / portrait / landscape and zoom
- **H** = open the app: camera roll, post public or to 3 paid tiers, add captions, reply to comments
- Likes, comments and followers roll in over time, and each post spreads further than the last
- Every pic gets a 0–100 daring score with a "why this score" breakdown
- Time skip buttons (30 min / 1 h / 5 h) so comments and DMs catch up

## Needs
- BepInEx 6 (IL2CPP be.788) + Custom Missions 2 (comes with its PhotoMode plugin)
- AI fans: NovelAI by default, but it's a standard OpenAI-style chat API so you can probably swap in your own model (local LLM, OpenRouter, etc.) in the config. No AI = canned comments
- **Premium judge (optional):** Ollama + the qwen2.5vl:7b vision model (~6 GB download, best with a GPU with 8 GB+ VRAM) so the AI actually looks at your pics. Without it, scoring uses game data
- Not sure how to set any of this up? Just ask an AI assistant to walk you through it

Both AI parts are just settings in the config, so when better open models come out you can swap them in and the fans and the judge get smarter for free.

## Install
1. Install BepInEx 6 (IL2CPP, be.788) and Custom Missions 2.
2. Download `OnlyFlash.zip` from Releases and extract it into the game folder (it contains `BepInEx\plugins\OnlyFlash.dll`, `BepInEx\plugins\PhotoMode.dll` and `CustomMissions2\OnlyFlash\`).
3. Start the game once, then set your AI settings in `BepInEx\config\OnlyFlash.cfg` (`ApiUrl`, `ApiKey`, `Model`; and `[AI Judge]` for Ollama).

## Building from source
.NET SDK 6+. Set `GameDir` in the `.csproj` files to your game folder (it references the BepInEx interop assemblies), then `dotnet build -c Release`.

*Inspired by FansChat*
