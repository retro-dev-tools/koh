# APU migration oracle

`oracle.cs` is a development-only generator. It references the unchanged managed
`Koh.Emulator.dll`; Rust does not execute or link it. Captured 2026-11-19 with
.NET SDK 10.0.115, source baseline `ae1b49a97722b69dbcdfd8aee9b023c96fc8ce62`.
The reference assembly SHA-256 is
`973a5dbe4f4ede0893a4adbde947e56835823b7afdb9e2292902576ed8512281`.

Each fixture contains sixteen 4096-T-cycle blocks. Each block contains the raw
156-byte managed APU state, reads of all 48 FF10–FF3F addresses, an i32 sample
count and little-endian mono i16 PCM. The trace exercises all channels, sweep
writes, DMG/CGB wave access, retrigger, restoration into a fresh instance,
power-off/on and 7-/15-bit noise. The first power-on has a high DIV-APU tap.

| Fixture | Bytes | SHA-256 |
| --- | ---: | --- |
| dmg.bin | 4618 | a27b1e598896b73ed2531661eecb83fab3d12f1336f418f36b7831bc7dc467b8 |
| cgb.bin | 4618 | 24cd96260f9267606c50e7d095fc0c4c77fd37a603f7add4304031401c0cb376 |

To regenerate, use a temporary net10.0 executable project, compile `oracle.cs`,
and add an assembly Reference with HintPath to a freshly built unchanged
Koh.Emulator.dll. Run with the fixture directory as the sole argument. Do not
use a ProjectReference or build in another worker's output directory.

The port preserves the reference's limitations: NR50/NR51 are stored but not
mixed; signed PCM wraps on conversion; the 8192-slot ring holds 8191 samples.
Save state omits sample queue, oscillator counters, envelope phase, sweep state
and CH3 sample latch/position. Restore preserves those omitted fields on the
receiving instance. Bounds checks reject malformed serialized channel values.
The timer supplies falling edges separately; the APU advances sequencer phase
even while powered off, gating only channel clocks.

## Managed test disposition

All rows are PORT replacements in `apu.rs`; no managed source is deleted here.
Source: `tests/Emulator/Koh.Emulator.Tests/ApuTests.cs` (27 methods).

| Managed method | Rust test |
| --- | --- |
| FrameSequencer_Step_Pattern_Fires_Expected_Clocks | frame_sequencer_step_pattern_fires_expected_clocks |
| SquareChannel_Produces_Nonzero_Output_After_Trigger | square_channel_produces_nonzero_output_after_trigger |
| LengthCounter_Disables_When_Reaches_Zero | length_counter_disables_when_reaches_zero |
| VolumeEnvelope_Increases_Toward_15 | volume_envelope_increases_toward_15_and_decreases_to_zero |
| NoiseChannel_Output_Varies | noise_channel_output_varies_and_width_bit_changes_lfsr |
| WaveChannel_Emits_Samples_From_Pattern | wave_channel_emits_samples_from_pattern_and_delays_first_sample |
| AudioSampleBuffer_Push_And_Drain_Roundtrip | audio_sample_buffer_push_and_drain_roundtrip |
| Apu_Disabled_Does_Not_Tick_Channels | nr52_power_and_disabled_writes |
| Apu_Enabled_Produces_Samples | pcm_square_literal_and_fractional_sample_clock |
| Nr52_Power_Bit_Reports_Status | nr52_power_and_disabled_writes |
| Apu_Write_Is_Ignored_When_Disabled | nr52_power_and_disabled_writes |
| WaveRam_Roundtrips_Through_FF30_FF3F | wave_ram_roundtrips_through_ff30_ff3f |
| Trigger_Does_Not_Reload_Running_Length_Counter | trigger_length_running_zero_and_skipped_step_reload |
| Trigger_Reloads_Length_To_Max_When_Counter_Is_Zero | trigger_length_running_zero_and_skipped_step_reload |
| Trigger_Reloads_Length_To_MaxMinusOne_When_Step_Skips_Length_And_Enabled | trigger_length_running_zero_and_skipped_step_reload |
| Nrx4_Enabling_Length_On_Skipped_Step_Extra_Clocks_And_Can_Disable | nrx4_extra_length_clock_can_disable_all_four_channels |
| PowerOff_Resets_DutyStep_But_Not_Length_Counter | power_off_resets_duty_but_preserves_lengths_wave_and_phase |
| Sweep_Overflow_Check_Runs_Immediately_On_Trigger_When_Shift_NonZero | sweep_overflow_check_runs_immediately_on_trigger |
| Sweep_Period_Zero_Skips_Periodic_Calculation | sweep_period_zero_skips_periodic_calculation |
| Sweep_Reads_Shift_And_Period_Live_From_Nr10_Not_Cached_At_Trigger | sweep_reads_live_shift_and_period_and_negate_exit_disables |
| Sweep_Exiting_Negate_After_Calculation_Disables_Channel | sweep_reads_live_shift_and_period_and_negate_exit_disables |
| WaveChannel_Trigger_Delays_Playback_By_One_Sample | wave_channel_emits_samples_from_pattern_and_delays_first_sample |
| WaveChannel_Retrigger_While_Reading_Corrupts_First_Four_Bytes | dmg_retrigger_corrupts_but_cgb_preserves_wave_ram |
| WaveRam_Access_While_Channel_On_Redirects_To_Current_Byte | dmg_wave_ram_access_window_is_one_tcycle_and_cgb_always_redirects |
| WaveRam_Access_During_JustRead_Window_Hits_Current_Byte | dmg_wave_ram_access_window_is_one_tcycle_and_cgb_always_redirects |
| Cgb_WaveRam_Access_While_Channel_On_Always_Redirects_No_Miss | dmg_wave_ram_access_window_is_one_tcycle_and_cgb_always_redirects |
| Cgb_WaveChannel_Retrigger_While_Reading_Does_Not_Corrupt | dmg_retrigger_corrupts_but_cgb_preserves_wave_ram |

Additional tests cover bounded overflow, register masks/DAC disable, sequencer
skip on high-DIV power-on, sweep register mirroring, exact integer sampling,
state field order/omissions, every truncated state prefix, malformed bounds,
and the independent managed trace. Timer divider phase/speed-switch edge
generation is owned by timer tests and system wiring, not asserted by APU-only
tests.
