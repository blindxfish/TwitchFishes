# TwitchFishes

TwitchFishes is a Unity aquarium overlay. Viewers get a fish when they chat, and their fish leave after 15 minutes of inactivity.

## Run the aquarium

1. Add the project folder in Unity Hub and open it with [Unity 6000.3.5f2](https://unity.com/releases/editor/whats-new/6000.3.5f2).
2. Open `Assets/Scenes/SampleScene.unity` and enter Play mode. The same scene is enabled for standalone builds.
3. Press **F2** in the Game view or standalone player to open **TwitchFishes Controls** in your browser. Use **Add demo fish** and **Drop food** there to try the aquarium offline.

## Capture the overlay in OBS

Launch the standalone player and add a **Game Capture** source in OBS. Set the mode to **Capture specific window**, select the **TwitchFishes** aquarium window, and add a green **Chroma Key** filter. The aquarium window contains only the keyed output and on-stream effects. Open the browser dashboard with **F2** whenever you need to adjust chat, rewards, or aquarium actions. [OBS Game Capture guide](https://obsproject.com/kb/game-capture-source)

The controls run on your own computer and are available while TwitchFishes is open. Demo and reward preview buttons deliberately run their actions in the captured aquarium.

## Connect with Twitch

1. Once per installation, register an application in the Twitch Developer Console. Choose Client Type **Public** and enter `http://localhost` as its redirect URL. Copy the app’s **Client ID** into **One-time Twitch app setup** in the browser dashboard. [Twitch app setup](https://github.com/twitchdev/drops-csharp-sample#configuration)
2. Choose **Connect with Twitch**, then **Authorize on Twitch**. Approve access using your broadcaster account. TwitchFishes connects chat and Channel Points rewards for that account. The access token goes directly to the app. [Twitch device authorization](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#device-code-grant-flow)
3. Choose **Remember sign-in on this device** before signing in if you want it to reconnect the next time you launch. On Windows, TwitchFishes protects the saved session with Windows user-account encryption. The app renews the session while it is running. Other desktop platforms keep the sign-in for the running session.

Use **Disconnect Twitch** to end the guided sign-in and remove its locally saved session. TwitchFishes keeps the public Client ID for the next sign-in.

### Manual tokens for a separate chat account

The browser dashboard still has **Use a chat token manually** and **Use a broadcaster token manually** for separate accounts. A chat token needs `chat:read`. A rewards token must come from the broadcaster and needs `channel:read:redemptions`. Both raw tokens and tokens prefixed with `oauth:` work.

The game saves the channel and chat account under Unity's persistent data folder. **Remember token locally** opts in to storing the token as plain text in the local `twitch-settings.json` file and reconnecting at launch. **Disconnect** turns off startup reconnect. The token field is masked.

An existing untracked `Assets/StreamingAssets/config.txt` with `channel`, `nick`, and `oauth` still works when there is no saved settings file. Move that file out of `StreamingAssets` before distributing a build because Unity copies that folder into the player.

## Set up Channel Points rewards

1. In the Twitch Creator Dashboard, open **Viewer Rewards → Channel Points → Manage Rewards**. Create a custom reward and set its title and point cost. For example, create **Feed the Fish**. [Twitch creator guide](https://help.twitch.tv/s/article/channel-points-guide)
2. Sign in to TwitchFishes as the broadcaster. The browser dashboard loads the custom rewards for your channel.
3. Select the reward in the list and choose **Join aquarium**, **Drop food**, **Show top fish**, or **Call dolphin**. Choose **No action** to remove a mapping. The list shows the point cost and availability for each custom reward.
4. Use **Preview** to try the action locally. **Refresh list** brings in rewards you create or edit on Twitch. The bottom of the panel shows the latest redemption.

Reward mappings are saved per channel and reward ID. For a manual rewards connection, **Remember broadcaster token locally** stores that token as plain text on this device and starts listening on launch. Use **Stop** to turn off the manual reward listener and its startup reconnect. Manage the Twitch redemption queue from the Creator Dashboard.

## Chat commands

| Command | What it does |
| --- | --- |
| `!food` | Drops a pellet. The fish that eats it earns one point. |
| `!topfish` | Shows the five highest scores for five seconds. |
| `!dolphin` | Sends a dolphin after a swimming fish. The fish loses 25 points and respawns after three seconds. The dolphin has a 60-second cooldown. |

Commands are case-insensitive. Scores last for the current session and reset when you switch channels.
