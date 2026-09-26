namespace Contracts;

// Events (carry a correlation id = EnrollmentId through the whole saga)
public record EnrollmentRequested(Guid EnrollmentId, string StudentId, Guid SectionId, decimal Tuition);
public record TuitionCharged(Guid EnrollmentId);
public record TuitionFailed(Guid EnrollmentId, string Reason);
