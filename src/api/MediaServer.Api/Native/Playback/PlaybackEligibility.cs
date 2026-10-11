using System.Linq.Expressions;
using MediaServer.Api.Remux;

namespace MediaServer.Api.Native.Playback;

/// <summary>The URL-free decision shared by playback negotiation and library SQL projections.</summary>
internal sealed record PlaybackFacts(
    bool HasPath, bool IsDisc, bool VideoSupported, bool HasAudio, bool AudioSupported,
    bool RangeSupported, bool DirectContainer, bool PackageVideo, bool PackageAudio,
    RemuxReadinessState Readiness);

internal static class PlaybackEligibility
{
    internal static readonly Expression<Func<PlaybackFacts, string?>> Reason = f =>
        !f.HasPath ? NativePlaybackReasons.NoFile :
        f.IsDisc ? NativePlaybackReasons.RequiresConversion :
        !f.VideoSupported ? NativePlaybackReasons.UnsupportedVideoCodec :
        !f.HasAudio ? NativePlaybackReasons.NoAudioTrack :
        !f.AudioSupported ? NativePlaybackReasons.UnsupportedAudioCodec :
        !f.RangeSupported ? NativePlaybackReasons.UnsupportedDynamicRange :
        f.DirectContainer ? null :
        !f.PackageVideo ? NativePlaybackReasons.PackagingUnsupportedVideo :
        !f.PackageAudio ? NativePlaybackReasons.PackagingUnsupportedAudio :
        f.Readiness == RemuxReadinessState.Pending ? NativePlaybackReasons.PackagingPending :
        f.Readiness != RemuxReadinessState.Ready ? NativePlaybackReasons.PackagingUnavailable : null;

    internal static readonly Func<PlaybackFacts, string?> Evaluate = Reason.Compile();

    internal sealed class Judged<T>
    {
        public T Facts { get; init; } = default!;
        public string? Reason { get; init; }
    }

    /// <summary>Inline the shared expression so EF executes it before filtering/counting/paging.</summary>
    internal static IQueryable<Judged<T>> Judge<T>(IQueryable<T> query, Expression<Func<T, PlaybackFacts>> facts)
    {
        var body = new ReduceFacts().Visit(new Replace(Reason.Parameters[0], facts.Body).Visit(Reason.Body))!;
        return query.Select(Expression.Lambda<Func<T, Judged<T>>>(
            Expression.MemberInit(Expression.New(typeof(Judged<T>)),
                Expression.Bind(typeof(Judged<T>).GetProperty(nameof(Judged<T>.Facts))!, facts.Parameters[0]),
                Expression.Bind(typeof(Judged<T>).GetProperty(nameof(Judged<T>.Reason))!, body)), facts.Parameters));
    }

    private sealed class ReduceFacts : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is NewExpression creation && creation.Type == typeof(PlaybackFacts))
            {
                var parameters = creation.Constructor!.GetParameters();
                var index = Array.FindIndex(parameters, p => p.Name == node.Member.Name);
                if (index >= 0) return Visit(creation.Arguments[index]);
            }
            return base.VisitMember(node);
        }
    }

    private sealed class Replace(Expression from, Expression to) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
    }
}
