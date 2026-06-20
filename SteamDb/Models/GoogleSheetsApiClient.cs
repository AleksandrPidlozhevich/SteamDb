using System;
using System.Threading;
using System.Threading.Tasks;
using SteamDb.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace SteamDb.Models;

public class GoogleSheetsApiClient
{
    private const string ApplicationName = "SteamDb";
    private const string CLIENT_ID = "400086036961-3uua9rt1oka9r2pahp7a0ljkohd46vhp.apps.googleusercontent.com";

    private readonly string[] _scopes =
    {
        SheetsService.Scope.Spreadsheets,
        DriveService.Scope.Drive
    };

    private readonly ISecretStore _secrets;
    private readonly string _userId;

    public GoogleSheetsApiClient(string userId = "user", ISecretStore? secretStore = null)
    {
        _userId = userId;
        _secrets = secretStore ?? new MsalSecretStore();
    }

    public SheetsService? SheetsService { get; private set; }
    public DriveService? DriveService { get; private set; }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = CLIENT_ID,
                    ClientSecret = null
                },
                _scopes,
                _userId,
                CancellationToken.None,
                new SecretDataStore(_secrets));

            SheetsService = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            DriveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            return true;
        }
        catch (Exception ex)
        {
            LogService.WriteError("Google authorization failed: " + ex.Message);
            return false;
        }
    }
}