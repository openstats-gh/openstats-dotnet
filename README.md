# openstats-dotnet
openstats SDK for .NET

## Usage

```csharp
// initialize an Openstats Client with your game's RID and a GameToken
// provided by the User
var client = new Openstats.Client() 
{
    GameRid = "g_30JsWkn0GHof1LXH62Idm",
    GameToken = userProvidedGameToken,
};

// start a Game Session for the game & user associated with the GameRid & GameToken
var gameSession = await client.StartSession();

// continuously send heatbeats for the game session to the openstats API
var pollTokenSource = new CancellationTokenSource();
Task.Run(() => gameSession.BeginPolling(pollTokenSource.Token));

// grab the user's latest achievement progress
// Note that we're using `client` not `gameSession`
var progress = await client.GetAchievementProgress(gameSession.User.Rid);
foreach (var (slug, progressValue) in progress) 
{
    // DisplayName might not be set, since its optional
    var userFriendlyName = gameSession.User.DisplayName ?? gameSession.User.Slug;  
    Console.WriteLine($"{userFriendlyName} has {progressValue} progress for achievement '{slug}'");
}

// update the user's achievement progress
progress = await gameSession.AddAchievementProgress(new Dictionary<string, int>
{
    ["beat-the-game"] = 1,
    ["defeat-lots-of-foes"] = 62,
});
```