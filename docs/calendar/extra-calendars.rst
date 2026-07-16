More Calendars
==============

`Foundry VTT's Simple Calendar module
<https://github.com/vigoren/foundryvtt-simple-calendar/tree/main/src/predefined-calendars>`_
ships several dozen more predefined calendars (Dark Sun, Eberron,
Exandria, Golarion, Greyhawk, Warhammer, ...). Its schema differs from
ours - use Settings' "Import calendar file..." button, which
auto-detects and converts a Simple Calendar export (see
``RpgTimeTracker.Shared/Services/SimpleCalendarImporter.cs`` for the
field mapping and its weekday-epoch approximations).

GM-authored calendar JSON (our own schema) can also be dropped directly
into ``%AppData%/RpgTimeTracker/Calendars`` (Linux/macOS:
``~/.config/RpgTimeTracker/Calendars``).
