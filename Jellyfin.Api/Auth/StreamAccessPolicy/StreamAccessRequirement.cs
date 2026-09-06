using Jellyfin.Api.Auth.DefaultAuthorizationPolicy;

namespace Jellyfin.Api.Auth.StreamAccessPolicy
{
    /// <summary>
    /// Authorization requirement for the media file endpoints.
    /// </summary>
    /// <remarks>
    /// Derives from <see cref="DefaultAuthorizationRequirement"/> on purpose: that makes
    /// <see cref="DefaultAuthorizationHandler"/> run for this requirement too, so an
    /// authenticated caller still gets the remote-access permission and parental-schedule
    /// checks. That handler deliberately does not Succeed for a subclassed requirement,
    /// leaving the decision to <see cref="StreamAccessHandler"/>, but a Fail() from it is
    /// final and cannot be undone here.
    /// </remarks>
    public class StreamAccessRequirement : DefaultAuthorizationRequirement
    {
        /// <summary>
        /// The name this policy is registered under.
        /// </summary>
        public const string PolicyName = "StreamAccess";
    }
}
