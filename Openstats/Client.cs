using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable MemberCanBePrivate.Global

namespace Openstats;

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global")]
public sealed record ErrorDetail(string Location, string Message, object Value);

public sealed record ProblemDetails(
    string Type,
    string? Title,
    string? Detail,
    int? Status,
    string? Instance,
    ErrorDetail[]? Errors);

public sealed class ProblemDetailsException(ProblemDetails problemDetails) : Exception(problemDetails.ToString());

public sealed class InvalidStateException(string message) : InvalidOperationException(message);

public sealed class MissingSessionTokenException() : Exception("No session token was returned from the server");

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global")]
public sealed record Game(string Rid, DateTimeOffset CreatedAt, string Slug);

public sealed record User(
    string Rid,
    DateTimeOffset CreatedAt,
    string Slug,
    string? DisplayName,
    string? AvatarUrl,
    string? BioText);

internal sealed record ProgressModel(Dictionary<string, int> Progress);

public sealed class GameSession
{
    private sealed record Model(string Rid, DateTimeOffset LastPulse, int NextPulseAfter, Game Game, User User);

    private enum State
    {
        None = 0,
        StartingSession = 1,
        SessionReady = 2,
        SendingPulse = 3,
    }

    private static State _state = State.None;
    private static readonly Lock StateLock = new();
    
    public string Rid => _lastModel.Rid;
    public DateTimeOffset LastPulse => _lastModel.LastPulse;
    public int NextPulseAfter => _lastModel.NextPulseAfter;
    public Game Game => _lastModel.Game;
    public User User => _lastModel.User;

    private readonly Uri _apiUrl;
    private Model _lastModel;
    private string _jwt;

    private GameSession(Uri apiUrl, Model lastModel, string jwt)
    {
        _apiUrl = apiUrl;
        _lastModel = lastModel;
        _jwt = jwt;
    }

    /// <summary>
    /// Starts a new Game Session.
    /// </summary>
    /// <param name="apiUrl"></param>
    /// <param name="gameToken"></param>
    /// <param name="gameRid"></param>
    /// <param name="userRid"></param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>The newly created <see cref="GameSession"/></returns>
    /// <exception cref="InvalidStateException">There is already an ongoing session or attempt to start a session.</exception>
    /// <exception cref="ProblemDetailsException">Received a non-success response from the Openstats API.</exception>
    /// <exception cref="MissingSessionTokenException">Received a success response from the API, but the API didn't respond with a Game Session Token.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">See <see cref="HttpClient.PostAsync(string, HttpContent, CancellationToken)"/>.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    /// <exception cref="JsonException">The response body is not valid JSON, or is somehow incompatible with the expected response type. See <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>.</exception>
    /// <remarks>
    /// After creating a session, you must occasionally send a session pulse to the API. <see cref="GameSession.BeginPolling"/> will
    /// continuously send pulses for you. <br/><br/>
    /// If you want more control over timing heartbeats, you can call <see cref="GameSession.SendHeartbeat"/> manually; however, you
    /// should try to send pulses at around <see cref="GameSession.NextPulseAfter"/> seconds after the
    /// <see cref="GameSession.LastPulse"/>.
    /// </remarks>
    /// <seealso cref="GameSession.SendHeartbeat"/>
    /// <seealso cref="GameSession.BeginPolling"/>
    public static async Task<GameSession> StartSession(
        Uri apiUrl,
        string gameToken,
        string gameRid,
        string userRid,
        CancellationToken cancellationToken = default)
    {
        if (!StateLock.TryEnter() || _state == State.StartingSession)
            throw new InvalidStateException("Cannot start a session while another session operation is in progress");
        
        if (_state > State.None)
            throw new InvalidStateException("Cannot start a session while another session exists");

        try
        {
            var encodedUserRid = HttpUtility.UrlPathEncode(userRid);
            var encodedGameRid = HttpUtility.UrlPathEncode(gameRid);
            var path = $"users/v1/{encodedUserRid}/games/{encodedGameRid}/sessions";

            var httpClient = new HttpClient();
            httpClient.BaseAddress = apiUrl;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {gameToken}");

            var response = await httpClient.PostAsync(path, null, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(json, Client.SerializerOptions);
                throw new ProblemDetailsException(problemDetails!);
            }

            response.Headers.TryGetValues("X-Game-Session-Token", out var sessionTokens);
            var jwt = sessionTokens?.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(jwt))
                throw new MissingSessionTokenException();

            var model = JsonSerializer.Deserialize<Model>(json, Client.SerializerOptions)!;

            _state = State.SessionReady;
            return new GameSession(apiUrl, model, jwt);
        }
        catch (Exception)
        {
            _state = State.None;
            throw;
        }
        finally
        {
            StateLock.Exit();
        }
    }
    
    public async Task SendHeartbeat(CancellationToken cancellationToken = default)
    {
        if (!StateLock.TryEnter() || _state != State.SessionReady)
            throw new InvalidStateException("Cannot send heartbeat while another session operation is in progress");

        try
        {
            _state = State.SendingPulse;

            var encodedUserRid = HttpUtility.UrlPathEncode(User.Rid);
            var encodedGameRid = HttpUtility.UrlPathEncode(Game.Rid);
            var encodedSessionRid = HttpUtility.UrlPathEncode(Rid);
            var path = $"users/v1/{encodedUserRid}/games/{encodedGameRid}/sessions/{encodedSessionRid}/heartbeat";

            var httpClient = new HttpClient();
            httpClient.BaseAddress = _apiUrl;
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_jwt}");

            var response = await httpClient.PostAsync(path, null, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(json, Client.SerializerOptions);
                throw new ProblemDetailsException(problemDetails!);
            }

            response.Headers.TryGetValues("X-Game-Session-Token", out var sessionTokens);
            _jwt = sessionTokens?.FirstOrDefault() ?? _jwt;
            
            _lastModel = JsonSerializer.Deserialize<Model>(json, Client.SerializerOptions)!;

            _state = State.SessionReady;
        }
        catch (Exception)
        {
            _state = State.None;
            throw;
        }
        finally
        {
            StateLock.Exit();
        }
    }
    
    public async Task BeginPolling(CancellationToken cancellationToken = default)
    {        
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(NextPulseAfter), cancellationToken);
            try
            {
                await SendHeartbeat(cancellationToken);
            }
            catch (InvalidStateException)
            {
                // we'll ignore InvalidStateException since it implies that SendHeartbeat was called manually at some
                // point, which isn't necessarily an error
            }
        }
    }
    
    /// <summary>
    /// Adds new achievement progress for the user associated with the current Game Session.
    /// </summary>
    /// <param name="newProgress">A Dictionary mapping Achievement Slugs to new progress values.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A Dictionary that maps Achievement Slugs to progress for each achievement that the user has progress in.</returns> 
    /// <exception cref="InvalidStateException">Thrown when there isn't an active session.</exception>
    /// <exception cref="ProblemDetailsException">Thrown when a non-success response is received from the Openstats API.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">See <see cref="HttpClient.PostAsync(string, HttpContent, CancellationToken)"/>.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    /// <exception cref="JsonException">The response body is not valid JSON, or is somehow incompatible with the expected response type. See <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>.</exception>
    /// <remarks>
    /// If a progress value is lower than the user's current progress for that achievement, then the value will be ignored.<br/><br/>
    /// If a progress value is higher than the achievement's progress requirement, then the value will be ignored.
    /// </remarks>
    /// <seealso cref="Client.GetAchievementProgress"/>
    public async Task<Dictionary<string, int>> AddAchievementProgress(Dictionary<string, int> newProgress,
        CancellationToken cancellationToken = default)
    {
        var encodedUserRid = HttpUtility.UrlPathEncode(User.Rid);
        var path = $"users/v1/{encodedUserRid}/achievements";
        
        var httpClient = new HttpClient();
        httpClient.BaseAddress = _apiUrl;
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_jwt}");

        var requestBody = JsonContent.Create(newProgress, options: Client.SerializerOptions);
        var response = await httpClient.PostAsync(path, requestBody, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(json, Client.SerializerOptions);
            throw new ProblemDetailsException(problemDetails!);
        }
        
        var progress = JsonSerializer.Deserialize<ProgressModel>(json, Client.SerializerOptions);
        return progress!.Progress;
    }
}

/// <summary>
/// A Client for interfacing with the Openstats API. Use this to create <see cref="GameSession"/>s, get or update
/// Achievement Progress, etc.
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
public sealed class Client
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// Your game's RID. Required for most Openstats API requests. You should provide this.
    /// </summary>
    public required string GameRid { get; init; }
    
    /// <summary>
    /// The user's Game Token. Required for some Openstats API requests. Your player should provide this to you. 
    /// </summary>
    public required string GameToken { get; init; }
    
    /// <summary>
    /// The URI to the Openstats instance you're using.
    /// </summary>
    public Uri ApiUrl { get; set; } = new("https://localhost:3000");

    /// <summary>
    /// Queries for some information about a specific User. 
    /// </summary>
    /// <param name="userRid">The RID of the user to query for. If set to '@me', will query for user info associated with the set <see cref="GameToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>The <see cref="User"/> retrieved from the Openstats API.</returns>
    /// <exception cref="ProblemDetailsException">Thrown when a non-success response is received from the Openstats API.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">See <see cref="HttpClient.GetAsync(string, CancellationToken)"/>.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    /// <exception cref="JsonException">The response body is not valid JSON, or is somehow incompatible with the expected response type. See <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>.</exception>
    public async Task<User> GetUser(string userRid = "@me", CancellationToken cancellationToken = default)
    {
        var encodedUserRid = HttpUtility.UrlPathEncode(userRid);
        var path = $"users/v1/{encodedUserRid}";

        var httpClient = new HttpClient();
        httpClient.BaseAddress = ApiUrl;
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GameToken}");
        
        var response = await httpClient.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(json, SerializerOptions);
            throw new ProblemDetailsException(problemDetails!);
        }
        
        var user = JsonSerializer.Deserialize<User>(json, SerializerOptions);
        return user!;
    }

    /// <summary>
    /// Queries for a User's achievement progress.
    /// </summary>
    /// <param name="userRid">The RID of the user to query achievement progress for.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A Dictionary that maps Achievement Slugs to progress for each achievement that the user has progress in.</returns> 
    /// <exception cref="ProblemDetailsException">Thrown when a non-success response is received from the Openstats API.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">See <see cref="HttpClient.GetAsync(string, CancellationToken)"/>.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    /// <exception cref="JsonException">The response body is not valid JSON, or is somehow incompatible with the expected response type. See <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>.</exception>
    /// <remarks>
    /// Achievements have a Slug that is unique among other achievements in the same game but aren't unique among all achievements.<br/><br/>
    /// The returned map will only contain entries for achievements that the user has progressed in. If a specific achievement isn't in the map, then you can assume that their progress in it is 0.
    /// </remarks>
    public async Task<Dictionary<string, int>> GetAchievementProgress(
        string userRid,
        CancellationToken cancellationToken = default)
    {
        var encodedUserRid = HttpUtility.UrlPathEncode(userRid);
        var path = $"users/v1/{encodedUserRid}/achievements";
        
        var httpClient = new HttpClient();
        httpClient.BaseAddress = ApiUrl;
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GameToken}");
        
        var response = await httpClient.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(json, SerializerOptions);
            throw new ProblemDetailsException(problemDetails!);
        }
        
        var progress = JsonSerializer.Deserialize<ProgressModel>(json, SerializerOptions);
        return progress!.Progress;
    }

    /// <summary>
    /// Starts a new Game Session.
    /// </summary>
    /// <param name="userRid">RID of the User to start a session for.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>The newly created <see cref="GameSession"/></returns>
    /// <exception cref="InvalidStateException">There is already an ongoing session or attempt to start a session.</exception>
    /// <exception cref="ProblemDetailsException">Received a non-success response from the Openstats API.</exception>
    /// <exception cref="MissingSessionTokenException">Received a success response from the API, but the API didn't respond with a Game Session Token.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">See <see cref="HttpClient.PostAsync(string, HttpContent, CancellationToken)"/>.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    /// <exception cref="JsonException">The response body is not valid JSON, or is somehow incompatible with the expected response type. See <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>.</exception>
    /// <remarks>
    /// After creating a session, you must occasionally send a session pulse to the API. <see cref="GameSession.BeginPolling"/> will
    /// continuously send pulses for you. <br/><br/>
    /// If you want more control over timing heartbeats, you can call <see cref="GameSession.SendHeartbeat"/> manually; however, you
    /// should try to send pulses at around <see cref="GameSession.NextPulseAfter"/> seconds after the
    /// <see cref="GameSession.LastPulse"/>.
    /// </remarks>
    /// <seealso cref="GameSession.SendHeartbeat"/>
    /// <seealso cref="GameSession.BeginPolling"/>
    public async Task<GameSession> StartSession(string userRid = "@me", CancellationToken cancellationToken = default)
        => await GameSession.StartSession(ApiUrl, GameToken, GameRid, userRid, cancellationToken);
}
