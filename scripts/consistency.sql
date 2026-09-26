-- Step 8: prove the two independent databases agree once the saga settles.

-- EnrollmentDb
SELECT Id, StudentId, Status FROM EnrollmentDb.dbo.Enrollments;
SELECT Code, Capacity, SeatsTaken FROM EnrollmentDb.dbo.Sections;

-- BillingDb
SELECT EnrollmentId, Amount, Status FROM BillingDb.dbo.TuitionCharges;

-- Side by side: every enrollment next to its billing outcome.
SELECT e.StudentId, e.Tuition, e.Status AS Enrollment, c.Status AS Billing
FROM EnrollmentDb.dbo.Enrollments e
LEFT JOIN BillingDb.dbo.TuitionCharges c ON c.EnrollmentId = e.Id
ORDER BY e.StudentId;

-- Invariants. Every row here should read OK.
SELECT 'Confirmed <-> Charged' AS [Check],
       CASE WHEN NOT EXISTS (
            SELECT 1 FROM EnrollmentDb.dbo.Enrollments e
            LEFT JOIN BillingDb.dbo.TuitionCharges c ON c.EnrollmentId = e.Id
            WHERE e.Status = 'Confirmed' AND ISNULL(c.Status, '') <> 'Charged')
       THEN 'OK' ELSE 'MISMATCH' END AS Result
UNION ALL
SELECT 'Cancelled <-> Declined',
       CASE WHEN NOT EXISTS (
            SELECT 1 FROM EnrollmentDb.dbo.Enrollments e
            LEFT JOIN BillingDb.dbo.TuitionCharges c ON c.EnrollmentId = e.Id
            WHERE e.Status = 'Cancelled' AND ISNULL(c.Status, '') <> 'Declined')
       THEN 'OK' ELSE 'MISMATCH' END
UNION ALL
SELECT 'No enrollment left Pending',
       CASE WHEN NOT EXISTS (SELECT 1 FROM EnrollmentDb.dbo.Enrollments WHERE Status = 'Pending')
       THEN 'OK' ELSE 'PENDING (saga still running?)' END
UNION ALL
SELECT 'No seat stranded (SeatsTaken = #Confirmed)',
       CASE WHEN NOT EXISTS (
            SELECT 1 FROM EnrollmentDb.dbo.Sections s
            WHERE s.SeatsTaken <> (SELECT COUNT(*) FROM EnrollmentDb.dbo.Enrollments e
                                   WHERE e.SectionId = s.Id AND e.Status <> 'Cancelled'))
       THEN 'OK' ELSE 'MISMATCH' END;
