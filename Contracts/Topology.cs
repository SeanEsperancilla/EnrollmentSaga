namespace Contracts;

public static class Topology
{
    public const string Exchange               = "saga.events";        // topic, durable
    public const string EnrollmentRequestedKey = "enrollment.requested";
    public const string TuitionChargedKey      = "tuition.charged";
    public const string TuitionFailedKey       = "tuition.failed";

    public const string BillingQueue    = "billing.queue";     // <- enrollment.requested
    public const string EnrollmentQueue = "enrollment.queue";  // <- tuition.charged, tuition.failed
    public const string AuditQueue      = "saga.audit";        // <- # (every event, for the console)
}
