namespace MarocLIS.Api.Contracts;

/// <summary>
/// The conventions of the API that are not in a request body: the routes, the header every
/// state-changing request carries, and the filter names of the worklist.
/// </summary>
public static class ApiProtocol
{
    /// <summary>
    /// The header every POST and DELETE must carry, with <see cref="RequestHeaderValue"/>.
    /// <para>
    /// The session cookie is SameSite=Strict, so a page from another site cannot make the phone's
    /// browser send it. The header is the second lock: a browser lets a page from another origin add
    /// a custom header only after a CORS preflight, and the API answers no preflight, so a forged
    /// form or script cannot produce a request the API accepts. It costs the real client one line.
    /// </para>
    /// </summary>
    public const string RequestHeader = "X-MarocLIS";

    public const string RequestHeaderValue = "1";

    /// <summary>On a page of a report preview: how many pages the report has.</summary>
    public const string PageCountHeader = "X-MarocLIS-Pages";

    public const string SessionRoute = "/api/session";
    public const string LanguageRoute = "/api/session/langue";
    public const string WorklistRoute = "/api/validation/worklist";

    /// <summary>Values of the <c>filtre</c> query parameter of the worklist: the chips of the screen.</summary>
    public static class Filters
    {
        public const string All = "tous";
        public const string Urgent = "urgents";
        public const string Abnormal = "anomalies";
        public const string QcBlocked = "cq";
    }

    /// <summary>The ways of telling a critical value the phone offers: both end with a read-back.</summary>
    public static class Channels
    {
        public const string Telephone = "Telephone";
        public const string InPerson = "InPerson";
    }

    /// <summary>The tone of a pill or a context line, from the design system: drives its colour.</summary>
    public static class Tones
    {
        public const string Ok = "ok";
        public const string Waiting = "waiting";
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Critical = "critical";
    }
}
