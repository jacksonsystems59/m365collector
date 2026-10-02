namespace M365Collector.Core;
// Serialized field names are unchanged so existing installed updaters remain compatible.
public sealed record UpdateRequest(string Zip, string Checksum, string TargetVersion, string PreviousVersion, int GuiProcessId);
public sealed record UpdateJournal(string State, string TargetVersion, string PreviousVersion);
