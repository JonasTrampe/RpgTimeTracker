RPG Time Tracker
================

.. image:: https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml/badge.svg
   :target: https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml
   :alt: Build
.. image:: https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/tests.yml/badge.svg
   :target: https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/tests.yml
   :alt: Tests
.. image:: https://img.shields.io/github/license/JonasTrampe/RpgTimeTracker
   :target: LICENSE
   :alt: License: MIT

**Status: 1.0.0.** See `CHANGELOG.md <CHANGELOG.md>`_ for a history of
notable changes.

.. include:: docs/_shared/pitch.rst

Requirements
------------

- `.NET 10 SDK <https://dotnet.microsoft.com/download>`_ (see
  ``global.json``; all three projects target ``net10.0``)
- Internet access for the first build (NuGet: Avalonia,
  CommunityToolkit.Mvvm, LibVLCSharp, Serilog)
- For video/audio playback (images always work without extra software):

  - **Windows**: nothing extra needed - native VLC is bundled via the
    ``VideoLAN.LibVLC.Windows`` NuGet package.
  - **Linux**: VLC must be installed system-wide, e.g.
    ``sudo apt install vlc`` (Debian/Ubuntu) or ``sudo dnf install vlc``
    (Fedora). Without it, images still work but video/audio sending
    errors instead of playing.

Building & Running
-------------------

.. code-block:: bash

   dotnet restore RpgTimeTracker.sln

   # GM/Host app:
   dotnet run --project RpgTimeTracker/RpgTimeTracker.csproj

   # Player client:
   dotnet run --project RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj

For a standalone Windows exe (for host and client respectively):

.. code-block:: bash

   dotnet publish RpgTimeTracker/RpgTimeTracker.csproj -c Release -r win-x64 --self-contained true
   dotnet publish RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj -c Release -r win-x64 --self-contained true

For Linux, use ``-r linux-x64`` accordingly.

Prebuilt ``win-x64``/``linux-x64`` archives for both apps are attached to
each `GitHub Release <https://github.com/JonasTrampe/RpgTimeTracker/releases>`_,
built the same way by ``.github/workflows/release.yml``.

Documentation
-------------

The full feature-by-feature documentation, an FAQ, and everything else
about what the software actually does lives on the hosted docs site:
https://JonasTrampe.github.io/RpgTimeTracker/

Contributor-facing material (architecture, wire protocol reference,
design-decision log) lives in `docs/internal/ <docs/internal/>`_.
