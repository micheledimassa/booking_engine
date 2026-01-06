namespace flight_booking.Models;

public static class BookingState
{
    public const string Received = "RECEIVED";
    public const string InventoryPending = "INVENTORY_PENDING";
    public const string InventoryApplied = "INVENTORY_APPLIED";
    public const string InventoryRejected = "INVENTORY_REJECTED";
    public const string FrappePending = "FRAPPE_PENDING";
    public const string Confirmed = "CONFIRMED";
    public const string Failed = "FAILED";
}
