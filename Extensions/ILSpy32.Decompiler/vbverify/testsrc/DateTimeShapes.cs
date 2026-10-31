using System;

public class DateTimeShapes {
	public DateTime Timestamp { get; set; }
	public DateTime? NullableTimestamp { get; set; }
	public System.DateTime FullyQualified { get; set; }
	public DateTime Method(DateTime input) => input;
}