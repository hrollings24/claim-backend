namespace ClaimBackend.Api.Auth;

public class CognitoOptions
{
    public const string SectionName = "Cognito";

    public required string Region { get; set; }
    public required string UserPoolId { get; set; }
    public required string AppClientId { get; set; }

    public string Authority => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";
}
