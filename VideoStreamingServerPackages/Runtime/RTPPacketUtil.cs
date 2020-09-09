﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public static class RTPPacketUtil
{

    public static void WriteHeader(byte[] rtp_packet, int rtp_version, int rtp_padding, int rtp_extension, int rtp_csrc_count, int rtp_marker, int rtp_payload_type)
    {
        rtp_packet[0] = (byte)((rtp_version << 6) | (rtp_padding << 5) | (rtp_extension << 4) | rtp_csrc_count);
        rtp_packet[1] = (byte)((rtp_marker << 7) | (rtp_payload_type & 0x7F));
    }

    public static void WriteSequenceNumber(byte[] rtp_packet, uint empty_sequence_id)
    {
        rtp_packet[2] = ((byte)((empty_sequence_id >> 8) & 0xFF));
        rtp_packet[3] = ((byte)((empty_sequence_id >> 0) & 0xFF));
    }

    public static void WriteTS(byte[] rtp_packet, uint ts)
    {
        rtp_packet[4] = ((byte)((ts >> 24) & 0xFF));
        rtp_packet[5] = ((byte)((ts >> 16) & 0xFF));
        rtp_packet[6] = ((byte)((ts >> 8) & 0xFF));
        rtp_packet[7] = ((byte)((ts >> 0) & 0xFF));
    }

    public static void WriteSSRC(byte[] rtp_packet, uint ssrc)
    {
        rtp_packet[8] = ((byte)((ssrc >> 24) & 0xFF));
        rtp_packet[9] = ((byte)((ssrc >> 16) & 0xFF));
        rtp_packet[10] = ((byte)((ssrc >> 8) & 0xFF));
        rtp_packet[11] = ((byte)((ssrc >> 0) & 0xFF));
    }
}
