using Konnichiwatt.Devices;

namespace Konnichiwatt.Tests;

/// <summary>Helpers: "21-BB-0B-1C" → bytes (same format as the probe logs).</summary>
internal static class Hex
{
    public static byte[] Bytes(string s) =>
        s.Split('-', ' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>A reply padded with zeros to <paramref name="length"/> bytes, like HID reports.</summary>
    public static byte[] Padded(string s, int length)
    {
        var b = Bytes(s);
        Array.Resize(ref b, Math.Max(length, b.Length));
        return b;
    }
}

/// <summary>HyperX Cloud Alpha Wireless — replies captured from the real dongle (03F0:098D) on 23/11/2026.</summary>
public class HyperXTests
{
    [Fact]
    public void Battery_reply_from_real_headset_is_28_percent()
    {
        var r = Hex.Padded("21-BB-0B-1C-0E-8E-01", 31);
        Assert.Equal((byte)28, HyperXProvider.ParseReply(r, 0x0B));
    }

    [Fact]
    public void Status_reply_means_headset_on() =>
        Assert.Equal((byte)0x02, HyperXProvider.ParseReply(Hex.Padded("21-BB-03-02", 31), 0x03));

    [Fact]
    public void Reply_to_another_command_is_ignored() =>
        Assert.Null(HyperXProvider.ParseReply(Hex.Padded("21-BB-0C-00", 31), 0x0B));

    [Theory]
    [InlineData(4200, true)]   // above 4107 mV: charging
    [InlineData(3500, false)]
    public void Cloud_Flight_voltage_curve_is_sane(int mv, bool above)
    {
        int pct = HyperXProvider.FlightPercent(mv);
        Assert.InRange(pct, 0, 100);
        if (above) Assert.Equal(100, pct);
    }
}

/// <summary>Logitech HID++ — the G903 reply is the real one captured from the LIGHTSPEED receiver (C539).</summary>
public class LogitechTests
{
    [Fact]
    public void G903_voltage_reply_3871mV_is_about_62_percent_discharging()
    {
        // 11 01 06 0B | 0F 1F 00 → 0x0F1F = 3871 mV, flags 0 (discharging)
        var r = Hex.Padded("11-01-06-0B-0F-1F-00", 20);
        var d = LogitechProvider.DecodeBattery(LogitechProvider.FeatVoltage, r);
        Assert.NotNull(d);
        Assert.Equal(62, d!.Value.Percent);
        Assert.Equal(ChargeState.Discharging, d.Value.State);
        Assert.True(d.Value.Approximate);
    }

    [Theory]
    [InlineData(0x80, ChargeState.Charging)]  // external power, sub-state 0 = charging
    [InlineData(0x81, ChargeState.Full)]      // sub-state 1 = end of charge
    public void Voltage_flags_decode_charging_states(byte flags, ChargeState expected)
    {
        var r = Hex.Padded($"11-01-06-0B-10-00-{flags:X2}", 20);
        Assert.Equal(expected, LogitechProvider.DecodeBattery(LogitechProvider.FeatVoltage, r)!.Value.State);
    }

    [Fact]
    public void Unified_battery_reports_percent_directly()
    {
        var r = Hex.Padded("11-01-05-1B-4B-04-01-00", 20); // 75 %, charging
        var d = LogitechProvider.DecodeBattery(LogitechProvider.FeatUnified, r)!.Value;
        Assert.Equal(75, d.Percent);
        Assert.Equal(ChargeState.Charging, d.State);
        Assert.False(d.Approximate);
    }

    [Fact]
    public void Unified_battery_without_percent_falls_back_to_level_flags()
    {
        var r = Hex.Padded("11-01-05-1B-00-04-00-00", 20); // 0 % reported, "good" level flag
        var d = LogitechProvider.DecodeBattery(LogitechProvider.FeatUnified, r)!.Value;
        Assert.Equal(50, d.Percent);
        Assert.True(d.Approximate);
    }

    [Fact]
    public void Headset_adc_below_curve_means_no_reading() =>
        Assert.Null(LogitechProvider.DecodeBattery(LogitechProvider.FeatAdc, Hex.Padded("11-FF-08-0B-0B-B8-01", 20))); // 3000 mV

    [Theory]
    [InlineData(4300, 100)]
    [InlineData(4186, 100)]
    [InlineData(3500, 0)]
    [InlineData(3000, 0)]
    public void Voltage_curve_edges(int mv, int expected) =>
        Assert.Equal(expected, LogitechProvider.VoltageToPercent(mv));

    [Fact]
    public void Voltage_curve_is_monotonic()
    {
        int prev = -1;
        for (int mv = 3400; mv <= 4300; mv += 10)
        {
            int p = LogitechProvider.VoltageToPercent(mv);
            Assert.True(p >= prev, $"{mv} mV gave {p} after {prev}");
            prev = p;
        }
    }
}

/// <summary>GameSir T4n Pro dongle (3537:103E) — frame captured while GameSir Connect read the battery.</summary>
public class GameSirTests
{
    private const string RealFrame = "06-0E-0A-01-03-01-06-52-06-B4-5E-5A-A0-64-00";

    [Fact]
    public void Real_device_info_frame_gives_mac_and_100_percent()
    {
        var info = GameSirProvider.ParseInfo(Hex.Padded(RealFrame, 64));
        Assert.NotNull(info);
        Assert.Equal("5206B45E5AA0", info!.Value.Mac);
        Assert.Equal(100, info.Value.Percent);
        Assert.False(info.Value.Charging);
    }

    [Fact]
    public void Battery_255_means_charging_without_level()
    {
        var info = GameSirProvider.ParseInfo(Hex.Padded(RealFrame.Replace("-64-00", "-FF-00"), 64))!.Value;
        Assert.True(info.Charging);
        Assert.Null(info.Percent);
    }

    [Theory]
    [InlineData("06-1A-0A-03-80-80-80-80")] // gamepad state stream
    [InlineData("06-04-0A-02-00")]          // ack of another command
    [InlineData("06-00-00-00-00")]          // idle
    public void Other_frames_are_not_device_info(string frame) =>
        Assert.Null(GameSirProvider.ParseInfo(Hex.Padded(frame, 64)));
}

/// <summary>Nintendo Switch Pro Controller: byte 2 of 0x21/0x30 reports.</summary>
public class SwitchTests
{
    [Theory]
    [InlineData(0x80, 100, ChargeState.Discharging)]
    [InlineData(0x90, 100, ChargeState.Full)]
    [InlineData(0x60, 70, ChargeState.Discharging)]
    [InlineData(0x41, 40, ChargeState.Discharging)] // low nibble is connection info, ignored
    [InlineData(0x30, 15, ChargeState.Charging)]
    [InlineData(0x00, 0, ChargeState.Discharging)]
    public void Status_byte(byte b, int pct, ChargeState state) =>
        Assert.Equal((pct, state), SwitchProvider.DecodeStatus(b));
}

/// <summary>PlayStation — offsets and formulas from Linux drivers/hid/hid-playstation.c.</summary>
public class PlayStationTests
{
    [Theory]
    [InlineData(0x05, 55, ChargeState.Discharging)]
    [InlineData(0x0A, 100, ChargeState.Discharging)]
    [InlineData(0x13, 35, ChargeState.Charging)]
    [InlineData(0x20, 100, ChargeState.Full)]
    [InlineData(0xF0, null, ChargeState.Unknown)]
    public void DualSense_status(byte st, int? pct, ChargeState state) =>
        Assert.Equal((pct, state), PlayStationProvider.DecodeDualSense(st));

    [Theory]
    [InlineData(0x07, 75, ChargeState.Discharging)]  // no cable
    [InlineData(0x17, 75, ChargeState.Charging)]     // cable, level 7
    [InlineData(0x1A, 100, ChargeState.Charging)]
    [InlineData(0x1B, 100, ChargeState.Full)]
    [InlineData(0x1E, null, ChargeState.Unknown)]
    public void DualShock4_status(byte st, int? pct, ChargeState state) =>
        Assert.Equal((pct, state), PlayStationProvider.DecodeDs4(st));

    [Fact]
    public void DualSense_usb_report_status_is_at_byte_53()
    {
        var r = new byte[64]; r[0] = 0x01; r[53] = 0x07;
        Assert.Equal((byte)0x07, PlayStationProvider.Status(r, dualSense: true, bt: false));
    }

    [Fact]
    public void DualSense_bluetooth_full_report_status_is_at_byte_54()
    {
        var r = new byte[78]; r[0] = 0x31; r[54] = 0x02;
        Assert.Equal((byte)0x02, PlayStationProvider.Status(r, dualSense: true, bt: true));
    }

    [Fact]
    public void Bluetooth_simple_report_has_no_battery()
    {
        var r = new byte[78]; r[0] = 0x01;
        Assert.Null(PlayStationProvider.Status(r, dualSense: true, bt: true));
    }

    [Fact]
    public void DS4_dongle_without_pad_is_ignored()
    {
        var r = new byte[64]; r[0] = 0x01; // sticks at 0 instead of resting at 0x80
        Assert.Null(PlayStationProvider.Status(r, dualSense: false, bt: false));
    }
}

/// <summary>Razer — report layout and CRC from OpenRazer (razercommon.c).</summary>
public class RazerTests
{
    [Fact]
    public void Battery_request_layout_and_crc()
    {
        var req = RazerProvider.BuildRequest(0x1F, 0x80);
        Assert.Equal(91, req.Length);       // report ID 0 + 90-byte razer_report
        Assert.Equal(0x1F, req[2]);         // transaction id
        Assert.Equal(0x02, req[6]);         // data size
        Assert.Equal(0x07, req[7]);         // class
        Assert.Equal(0x80, req[8]);         // command: get battery level
        Assert.Equal(0x02 ^ 0x07 ^ 0x80, req[89]); // XOR of report bytes 2..87
    }

    [Fact]
    public void Successful_reply_returns_args1()
    {
        var resp = new byte[91]; resp[1] = 0x02; resp[7] = 0x07; resp[8] = 0x80; resp[10] = 200;
        Assert.Equal((false, (int?)200), RazerProvider.ParseReply(resp, 0x80));
    }

    [Fact]
    public void Busy_reply_asks_to_retry()
    {
        var resp = new byte[91]; resp[1] = 0x01;
        Assert.True(RazerProvider.ParseReply(resp, 0x80).Busy);
    }

    [Fact]
    public void Failure_or_other_command_gives_no_value()
    {
        var resp = new byte[91]; resp[1] = 0x02; resp[7] = 0x07; resp[8] = 0x84; resp[10] = 1;
        Assert.Null(RazerProvider.ParseReply(resp, 0x80).Value);
        resp[1] = 0x04; // timeout (device asleep)
        Assert.Null(RazerProvider.ParseReply(resp, 0x84).Value);
    }
}

/// <summary>SteelSeries — offsets from HeadsetControl (replies in hidapi form, no leading 0 byte).</summary>
public class SteelSeriesTests
{
    [Fact]
    public void Nova7_percent_family()
    {
        var r = Hex.Padded("B0-00-49-03", 128); // 73 %, on battery
        Assert.Equal((73, ChargeState.Discharging), SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Nova7Percent, r)!.Value);
    }

    [Fact]
    public void Nova7_discrete_family_and_headset_off()
    {
        Assert.Equal((75, ChargeState.Charging),
            SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Nova7Discrete, Hex.Padded("B0-00-03-01", 128))!.Value);
        Assert.Null(SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Nova7Discrete, Hex.Padded("B0-00-03-00", 128)));
    }

    [Fact]
    public void Arctis9_scales_raw_range()
    {
        Assert.Equal(100, SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Arctis9, Hex.Padded("00-00-00-9A-00", 12))!.Value.Pct);
        Assert.Equal(0, SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Arctis9, Hex.Padded("00-00-00-64-00", 12))!.Value.Pct);
        Assert.Null(SteelSeriesProvider.Decode(SteelSeriesProvider.Family.Arctis9, Hex.Padded("00-00-00-80-FF", 12)));
    }

    [Fact]
    public void Nova_Pro_Wireless_eighths()
    {
        var r = new byte[128]; r[6] = 6; r[15] = 0x02;
        Assert.Equal((75, ChargeState.Charging), SteelSeriesProvider.Decode(SteelSeriesProvider.Family.NovaProWireless, r)!.Value);
        r[15] = 0x01;
        Assert.Null(SteelSeriesProvider.Decode(SteelSeriesProvider.Family.NovaProWireless, r));
    }

    [Fact]
    public void Every_family_has_a_request()
    {
        foreach (var f in Enum.GetValues<SteelSeriesProvider.Family>())
            Assert.NotEmpty(SteelSeriesProvider.BatteryRequest(f).Request);
    }
}

/// <summary>Corsair headsets — HeadsetControl formats.</summary>
public class CorsairTests
{
    [Theory]
    [InlineData("64-64-D5-00-01", 85, ChargeState.Discharging)] // 0xD5 = mic-up bit + 85
    [InlineData("64-64-32-00-05", 50, ChargeState.Charging)]
    public void Void_reply(string frame, int pct, ChargeState state) =>
        Assert.Equal((pct, state), CorsairHeadsetProvider.DecodeVoid(Hex.Bytes(frame))!.Value);

    [Fact]
    public void Void_headset_off() => Assert.Null(CorsairHeadsetProvider.DecodeVoid(Hex.Bytes("64-64-50-00-00")));

    [Theory]
    [InlineData("02-09-02-00-6F-02", 62)]   // 0x026F = 623 tenths
    [InlineData("02-09-02-00-E8-03", 100)]  // 1000
    [InlineData("02-09-02-00-00-00", null)] // 0 → invalid
    public void V2_tenths_of_percent(string frame, int? pct) =>
        Assert.Equal(pct, CorsairHeadsetProvider.DecodeV2(Hex.Bytes(frame)));
}
