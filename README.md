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
3. Start the game once, then set your AI settings in `BepInEx\config\OnlyFlash.cfg` (`ApiUrl`, `ApiKey`, `Model`; and `[AI Judge]` for Ollama). See **AI setup** below.
4. In game, open OnlyFlash (**H**) and press **Test AI fans**. It tells you straight away whether your AI works, and if not, why.

## AI setup
The `[AI]` settings are blank on purpose: blank means NovelAI with the `glm-4-6` model. Pick one of these:

**A. NovelAI (paid plan)**
```
ApiKey = your Persistent API Token (NovelAI > Account Settings > Get Persistent API Token)
```
Leave `ApiUrl` and `Model` blank. **The NovelAI free trial does not work in mods**: NovelAI only lets trial accounts generate on its own website (you get `Recaptcha token is required for trial generations`). Any paid plan works.

**B. Free, on your own PC with Ollama** (the same Ollama the AI Judge uses)
```
ApiUrl = http://localhost:11434/v1/chat/completions
ApiKey =
Model = qwen2.5vl:7b
```
`qwen2.5vl:7b` is the judge's model, so there's nothing extra to download and Ollama doesn't have to swap models. Its comments are tamer than NovelAI's. With a big GPU you can pull a bigger or less filtered text model instead (`ollama pull <name>`) and put its name in `Model`.

**C. Anything else that speaks the OpenAI chat API** (OpenRouter, LM Studio, koboldcpp, ...): put its chat completions URL, key and model name in the three settings.

Restart the game after changing the config.

**Still canned comments?** Press **Test AI fans** in the app, or look for lines starting with `OnlyFlash AI:` in `BepInEx\LogOutput.log`. They say what went wrong in plain words (wrong key, model not downloaded, Ollama not running, too slow, and so on). If a local model is slow, raise `TimeoutSeconds`.

## Building from source
.NET SDK 6+. Set `GameDir` in the `.csproj` files to your game folder (it references the BepInEx interop assemblies), then `dotnet build -c Release`.

*Inspired by FansChat*
