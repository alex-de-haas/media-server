using MediaServer.Api.Hosty;

namespace MediaServer.Api.Native;

/// <summary>
/// Marks an endpoint as safe to serve on the <em>public</em> binding. Kestrel listens on both the
/// internal and the public port with one shared route table, so without this every internal route —
/// catalog administration, torrents, conversions — is reachable from the internet, held shut only by
/// Host identity.
///
/// The check is a positive list expressed as endpoint metadata rather than a path prefix, so a route
/// group added later is unpublished until somebody marks it deliberately. That is the safe direction
/// for the mistake to fall. See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public sealed class PublicSurfaceAttribute : Attribute;

public static class PublicSurface
{
    /// <summary>Publishes every endpoint in the group on the public binding.</summary>
    public static TBuilder AllowPublic<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PublicSurfaceAttribute());
        return builder;
    }

    /// <summary>
    /// Refuses unpublished endpoints on the public binding with <c>404</c> — not <c>401</c>, because the
    /// existence of an administration surface is not something an unauthenticated caller needs
    /// confirmed. Runs after routing (so the endpoint is known) and before authentication (so an
    /// unpublished route never even looks at a credential).
    /// </summary>
    public static IApplicationBuilder UsePublicSurfaceAllowlist(this IApplicationBuilder app, HostyOptions hosty)
    {
        var publicPort = hosty.PublicBindPort;
        var internalPort = hosty.InternalBindPort;

        // Nothing to gate when there is no public binding, or when both surfaces share one port and
        // splitting them would be a guess rather than a decision.
        if (publicPort is null || publicPort == internalPort)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            var published = context.GetEndpoint()?.Metadata.GetMetadata<PublicSurfaceAttribute>() is not null;
            if (ShouldRefuse(context.Connection.LocalPort, publicPort.Value, internalPort, published))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// The whole decision, kept pure so it can be asserted without a host: a request is refused when it
    /// arrived on the public binding and matched an endpoint nobody published.
    /// </summary>
    internal static bool ShouldRefuse(int localPort, int publicPort, int? internalPort, bool published) =>
        localPort == publicPort && localPort != internalPort && !published;
}
