# openstats-dotnet
openstats SDK for .NET

## Installing

WIP

## Getting Started

Basic usage looks like this:

```csharp
var client = new Openstats.Client() 
{
    GameRid = "g_30JsWkn0GHof1LXH62Idm",
    GameToken = userProvidedGameToken,
};

var user = await client.GetUser();
client.UserRid = user.Rid;

await client.StartSession();

var pollTokenSource = new CancellationTokenSource();
Task.Run(() => client.BeginPolling(pollTokenSource.Token));

// grab the user's latest achievement progress
var progress = await client.GetAchievementProgress(user.Rid);

// update their progress
progress = await client.AddAchievementProgress(new Dictionary<string, int>
{
    ["beat-the-game"] = 1,
    ["defeat-lots-of-foes"] = 62,
});
```