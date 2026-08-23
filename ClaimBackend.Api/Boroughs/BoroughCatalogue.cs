namespace ClaimBackend.Api.Boroughs;

/// <summary>
/// Where a borough sits relative to the centre. Shown on the board so players can see at a
/// glance how far a borough is likely to be; it carries no rule of its own.
/// </summary>
public enum BoroughZone
{
    Inner,
    Outer,
}

public record Borough(string Id, string Name, BoroughZone Zone);

public static class BoroughCatalogue
{
    /// <summary>
    /// The 32 London boroughs. Inner and outer follow the statutory split, which puts twelve
    /// boroughs inner and twenty outer. The City of London is deliberately absent — it is not a
    /// borough, and at a square mile it makes a poor territory to travel to and claim.
    /// </summary>
    public static readonly IReadOnlyList<Borough> All =
    [
        new("camden", "Camden", BoroughZone.Inner),
        new("greenwich", "Greenwich", BoroughZone.Inner),
        new("hackney", "Hackney", BoroughZone.Inner),
        new("hammersmith-and-fulham", "Hammersmith and Fulham", BoroughZone.Inner),
        new("islington", "Islington", BoroughZone.Inner),
        new("kensington-and-chelsea", "Kensington and Chelsea", BoroughZone.Inner),
        new("lambeth", "Lambeth", BoroughZone.Inner),
        new("lewisham", "Lewisham", BoroughZone.Inner),
        new("southwark", "Southwark", BoroughZone.Inner),
        new("tower-hamlets", "Tower Hamlets", BoroughZone.Inner),
        new("wandsworth", "Wandsworth", BoroughZone.Inner),
        new("westminster", "Westminster", BoroughZone.Inner),

        new("barking-and-dagenham", "Barking and Dagenham", BoroughZone.Outer),
        new("barnet", "Barnet", BoroughZone.Outer),
        new("bexley", "Bexley", BoroughZone.Outer),
        new("brent", "Brent", BoroughZone.Outer),
        new("bromley", "Bromley", BoroughZone.Outer),
        new("croydon", "Croydon", BoroughZone.Outer),
        new("ealing", "Ealing", BoroughZone.Outer),
        new("enfield", "Enfield", BoroughZone.Outer),
        new("haringey", "Haringey", BoroughZone.Outer),
        new("harrow", "Harrow", BoroughZone.Outer),
        new("havering", "Havering", BoroughZone.Outer),
        new("hillingdon", "Hillingdon", BoroughZone.Outer),
        new("hounslow", "Hounslow", BoroughZone.Outer),
        new("kingston-upon-thames", "Kingston upon Thames", BoroughZone.Outer),
        new("merton", "Merton", BoroughZone.Outer),
        new("newham", "Newham", BoroughZone.Outer),
        new("redbridge", "Redbridge", BoroughZone.Outer),
        new("richmond-upon-thames", "Richmond upon Thames", BoroughZone.Outer),
        new("sutton", "Sutton", BoroughZone.Outer),
        new("waltham-forest", "Waltham Forest", BoroughZone.Outer),
    ];

    private static readonly Dictionary<string, Borough> ById =
        All.ToDictionary(borough => borough.Id);

    public static Borough? Find(string id) => ById.GetValueOrDefault(id);

    public static string NameOf(string id) => Find(id)?.Name ?? id;
}
