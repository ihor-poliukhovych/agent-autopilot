using Catalogue.Api.Store;

namespace Catalogue.Api.Members;

public static class MemberEndpoints
{
    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/members/{id:int}", GetMember);
        return app;
    }

    private static IResult GetMember(int id, CatalogueStore store) =>
        store.FindMember(id) is { } member
            ? Results.Ok(member)
            : Results.Problem($"Member {id} was not found.", statusCode: StatusCodes.Status404NotFound);
}
