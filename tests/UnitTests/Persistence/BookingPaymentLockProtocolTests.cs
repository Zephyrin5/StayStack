using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// A change-detector for a protocol, in the same spirit as
// SoftDeleteFilterShapeTests: it pins an invariant rather than exercising a
// path, because the failure it watches for is invisible from the file that
// causes it.
//
// BookingPaymentLock only excludes initiation from a cancellation if every path
// that cancels a booking takes it. ExpireUnpaidBookingsJob was added as a
// cancelling path, the lock was later added to CancelBookingHandler, and the job
// was not updated - it held the booking row alone, which initiation never
// touches, so the two raced freely. Every file looked complete on its own.
//
// Source-based, deliberately. The lock is a SQL statement issued through Dapper;
// nothing about it is visible to reflection, and a behavioural test per path is
// what the next path's author would forget to write. This one finds the new
// path by itself.
public partial class BookingPaymentLockProtocolTests
{
    // Booking.Cancel takes the cancellation instant, so a call with arguments.
    // CancellationTokenSource.Cancel() has none and is not matched; if some
    // other Cancel(x) ever lands in src, this test names the file and the
    // author confirms it is not a booking.
    [GeneratedRegex(@"\.Cancel\(\s*[^)\s]")]
    private static partial Regex BookingCancelCall();

    [GeneratedRegex(@"BookingPaymentLock\.KeyFor\(")]
    private static partial Regex PaymentLockAcquisition();

    [GeneratedRegex(@"FOR UPDATE")]
    private static partial Regex RowLock();

    // Comments are stripped first (SourceTree.WithoutComments): this codebase
    // explains its locks at length, and "FOR UPDATE" or
    // "BookingPaymentLock.KeyFor(" appearing in prose must not satisfy - or
    // reorder - anything. Strings are kept, since the row lock is SQL.
    private static IEnumerable<(string Path, string Code)> CancellingPaths() =>
        SourceTree.SourceFiles()
            .Select(path => (Path: path, Code: SourceTree.WithoutComments(File.ReadAllText(path))))
            .Where(file => BookingCancelCall().IsMatch(file.Code));

    [Fact]
    public void EveryPathThatCancelsABooking_TakesThePaymentLock_BeforeTheRowLockAndTheCancel()
    {
        List<(string Path, string Code)> paths = CancellingPaths().ToList();

        // Not vacuous: a scan that found nothing would pass every assertion
        // below. The two known paths must be among what it found - and the loop
        // then asserts over every path discovered, so a third cancelling path
        // is checked without anyone adding it here.
        Assert.Contains(paths, p => Path.GetFileName(p.Path) == "CancelBookingHandler.cs");
        Assert.Contains(paths, p => Path.GetFileName(p.Path) == "ExpireUnpaidBookingsJob.cs");

        foreach ((string path, string code) in paths)
        {
            string name = Path.GetFileName(path);

            Match lockTaken = PaymentLockAcquisition().Match(code);

            Assert.True(lockTaken.Success,
                $"{name} cancels a booking without taking BookingPaymentLock, so initiation cannot see it. " +
                "Take the lock in the same transaction as the cancellation - see BookingPaymentLock.");

            Assert.True(lockTaken.Index < BookingCancelCall().Match(code).Index,
                $"{name} takes BookingPaymentLock after cancelling, which excludes nothing.");

            // Two locks, one order: the payment lock, then the booking row.
            Match rowLock = RowLock().Match(code);

            Assert.True(!rowLock.Success || lockTaken.Index < rowLock.Index,
                $"{name} takes the booking row lock before BookingPaymentLock. Every path taking both takes " +
                "the advisory lock first - see BookingPaymentLock.");
        }
    }
}
