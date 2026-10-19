using System.Buffers.Binary;
using UnitSport.Concert.Protocols;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Art-Net, sACN and OSC as a concert's show input reads them (#885): what Chataigne and TouchDesigner send.</summary>
public class ShowProtocolsTests
{
    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void ArtDmx_round_trips_with_a_15_bit_port_address()
    {
        var slots = Ramp(512);
        var packet = ArtNet.EncodeDmx(0x1234 & 0x7fff, 9, slots);
        Assert.Equal(ArtNet.OpDmx, ArtNet.OpCode(packet));
        var dmx = ArtNet.ParseDmx(packet)!.Value;
        Assert.Equal(0x1234, dmx.Universe);
        Assert.Equal(9, dmx.Sequence);
        Assert.Equal(512, dmx.Length);
        Assert.Equal(slots, dmx.Data);
    }

    [Fact]
    public void ArtDmx_rounds_an_odd_length_up_and_rejects_other_packets()
    {
        var packet = ArtNet.EncodeDmx(0, 0, Ramp(3));
        Assert.Equal(18 + 4, packet.Length);
        Assert.Equal(4, ArtNet.ParseDmx(packet)!.Value.Length);
        Assert.Null(ArtNet.ParseDmx("not art-net at all, no"u8));
        var poll = new byte[14];
        "Art-Net\0"u8.CopyTo(poll);
        BinaryPrimitives.WriteUInt16LittleEndian(poll.AsSpan(8), ArtNet.OpPoll);
        Assert.Equal(ArtNet.OpPoll, ArtNet.OpCode(poll));
        Assert.Null(ArtNet.ParseDmx(poll));
    }

    [Fact]
    public void ArtPollReply_is_239_bytes_and_names_its_universes()
    {
        var reply = ArtNet.EncodePollReply(new byte[] { 192, 168, 1, 20 }, "UnitSport", "UnitSport concert rig", new[] { 0, 1, 2 });
        Assert.Equal(239, reply.Length);
        Assert.Equal(ArtNet.OpPollReply, ArtNet.OpCode(reply));
        Assert.Equal(new byte[] { 192, 168, 1, 20 }, reply[10..14]);
        Assert.Equal(3, reply[173]);
        Assert.Equal(2, reply[192]);
    }

    [Fact]
    public void Sacn_round_trips_and_names_its_multicast_group()
    {
        var slots = Ramp(512);
        var packet = Sacn.EncodeData(650, 42, slots, "TouchDesigner", Guid.NewGuid());
        Assert.Equal(638, packet.Length);
        var dmx = Sacn.ParseData(packet)!.Value;
        Assert.Equal(650, dmx.Universe);
        Assert.Equal(42, dmx.Sequence);
        Assert.Equal(slots, dmx.Data);
        Assert.Equal("239.255.2.138", Sacn.Multicast(650));
    }

    [Fact]
    public void Sacn_skips_a_terminated_stream_and_preview_data()
    {
        var packet = Sacn.EncodeData(1, 0, Ramp(16), "Chataigne", Guid.Empty);
        packet[112] = 0x40;
        Assert.Null(Sacn.ParseData(packet));
        packet[112] = 0x80;
        Assert.Null(Sacn.ParseData(packet));
    }

    [Fact]
    public void Osc_message_round_trips_int_float_string_and_booleans()
    {
        var packet = Osc.Encode("/concert/section", 3, 0.5f, "chorus", true);
        var m = Assert.Single(Osc.Parse(packet));
        Assert.Equal("/concert/section", m.Address);
        Assert.Equal(new object[] { 3, 0.5f, "chorus", true }, m.Args);
        Assert.Equal(0.5f, m.Float(1));
        Assert.Equal(3, m.Int(0));
        Assert.Equal(1f, m.Float(3));
    }

    [Fact]
    public void Osc_pads_a_four_byte_string_with_four_zeros()
    {
        // "/bpm" is 4 bytes: the terminator needs a whole new word
        var packet = Osc.Encode("/bpm", 128f);
        Assert.Equal(8 + 4 + 4, packet.Length);
        Assert.Equal(128f, Assert.Single(Osc.Parse(packet)).Float(0));
    }

    [Fact]
    public void Osc_bundles_nest_and_their_messages_come_out_in_order()
    {
        byte[] Bundle(params byte[][] elements)
        {
            var ms = new MemoryStream();
            ms.Write("#bundle\0"u8);
            ms.Write(new byte[8]);
            foreach (var e in elements)
            {
                var size = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(size, e.Length);
                ms.Write(size);
                ms.Write(e);
            }
            return ms.ToArray();
        }
        var packet = Bundle(Osc.Encode("/concert/go"), Bundle(Osc.Encode("/concert/bpm", 126f), Osc.Encode("/concert/blackout", 1)));
        var all = Osc.Parse(packet);
        Assert.Equal(new[] { "/concert/go", "/concert/bpm", "/concert/blackout" }, all.Select(m => m.Address));
        Assert.Empty(Osc.Parse("garbage!"u8));
    }
}
