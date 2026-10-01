/* Copyright (C) 2014-2019 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 *
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 *
 * WebdavSharp patch: minimal [MS-RAP] NetShareEnum implementation for
 * \PIPE\LANMAN, required by CX File Explorer (JCIFS-based) and other
 * clients that enumerate shares via SMB1 RAP instead of srvsvc RPC.
 * Upstream TransactionHelper returns STATUS_NOT_IMPLEMENTED here, which
 * makes such clients fail the server-root listing ("load error").
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace SMBLibrary.Server.SMB1
{
    /// <summary>
    /// Minimal [MS-RAP] Remote Administration Protocol helper (RAP NetShareEnum only).
    /// Pure codec: no server state, fully unit-testable.
    /// References: [MS-RAP] 2.5.6.1.1 (request), 2.5.6.3.1/2 (info 0/1), 3.2.5.1 (server logic).
    /// </summary>
    public static class RapHelper
    {
        public const ushort RapOpNetShareEnum = 0;

        public const ushort Win32Success = 0x0000;
        public const ushort Win32InvalidParameter = 0x0057; // ERROR_INVALID_PARAMETER
        public const ushort Win32InvalidLevel = 0x007C; // ERROR_INVALID_LEVEL
        public const ushort Win32MoreData = 0x00EA; // ERROR_MORE_DATA

        public const ushort StypeDiskTree = 0x0000;
        public const ushort StypePrintQueue = 0x0001;
        public const ushort StypeDevice = 0x0002;
        public const ushort StypeIpc = 0x0003;

        /// <summary>A share to advertise via NetShareEnum.</summary>
        public sealed class ShareEntry
        {
            public ShareEntry(string name, ushort type, string remark)
            {
                Name = name;
                Type = type;
                Remark = remark;
            }

            public readonly string Name;
            public readonly ushort Type;
            public readonly string Remark;
        }

        /// <summary>
        /// Build the TRANS response parameter/data blocks for a RAP NetShareEnum request.
        /// Always produces a response (errors are reported via the RAP Win32ErrorCode
        /// in the response parameters, per [MS-RAP] 3.2.5.1); the SMB status stays SUCCESS.
        /// </summary>
        /// <param name="requestParameters">Reassembled TRANS parameters (RAP request).</param>
        /// <param name="maxDataCount">TRANS MaxDataCount: upper bound for the response data block.</param>
        /// <param name="shares">Candidate shares (already filtered, e.g. IPC$ excluded).</param>
        /// <param name="responseParameters">8-byte RAPOutParams: status/convert/returned/available.</param>
        /// <param name="responseData">Packed NetShareInfo0/1 array + NUL-terminated remarks.</param>
        public static void GetNetShareEnumResponse(byte[] requestParameters, int maxDataCount,
            IList<ShareEntry> shares, out byte[] responseParameters, out byte[] responseData)
        {
            ushort level;
            ushort receiveBufferSize;
            string validationError = ValidateNetShareEnumRequest(requestParameters, out level, out receiveBufferSize);
            if (validationError == "level")
            {
                GetErrorResponse(Win32InvalidLevel, out responseParameters, out responseData);
                return;
            }
            if (validationError != null)
            {
                GetErrorResponse(Win32InvalidParameter, out responseParameters, out responseData);
                return;
            }

            // Spec: names that do not fit the 13-byte field MUST NOT be included.
            List<ShareEntry> eligible = new List<ShareEntry>();
            List<byte[]> names = new List<byte[]>();
            for (int i = 0; i < shares.Count; i++)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(shares[i].Name ?? String.Empty);
                if (nameBytes.Length > 12)
                    continue;
                eligible.Add(shares[i]);
                names.Add(nameBytes);
            }

            int entrySize = (level == 0) ? 13 : 20;
            int cap = receiveBufferSize;
            if (maxDataCount >= 0 && maxDataCount < cap)
                cap = maxDataCount;

            // Remark strings are packed after the COMPLETE fixed-size entry array
            // ([MS-RAP] 2.5.11), so offsets need the final entry count up front:
            // first decide how many entries fit, then write them.
            List<byte[]> remarkList = new List<byte[]>();
            for (int i = 0; i < eligible.Count; i++)
                remarkList.Add(Encoding.ASCII.GetBytes(eligible[i].Remark ?? String.Empty));

            int fitted = 0;
            int packed = 0;
            for (int i = 0; i < eligible.Count; i++)
            {
                int need = entrySize + ((level == 0) ? 0 : remarkList[i].Length + 1);
                if (packed + need > cap)
                    break;
                packed += need;
                fitted++;
            }

            List<byte> entries = new List<byte>();
            List<byte> strings = new List<byte>();
            for (int i = 0; i < fitted; i++)
            {
                if (level == 0)
                {
                    WriteName13(entries, names[i]);
                }
                else
                {
                    // Offsets are converter-relative; converter is 0 (impacket-compatible).
                    int remarkOffset = entrySize * fitted + strings.Count;
                    WriteName13(entries, names[i]);
                    entries.Add(0); // Pad
                    WriteU16(entries, eligible[i].Type);
                    WriteU16(entries, (ushort)remarkOffset);
                    WriteU16(entries, 0); // RemarkOffsetHigh, unused
                    for (int b = 0; b < remarkList[i].Length; b++)
                        strings.Add(remarkList[i][b]);
                    strings.Add(0);
                }
            }

            ushort status = (fitted < eligible.Count) ? Win32MoreData : Win32Success;
            responseParameters = new byte[8];
            WriteU16At(responseParameters, 0, status);
            WriteU16At(responseParameters, 2, 0); // Converter
            WriteU16At(responseParameters, 4, (ushort)fitted);
            WriteU16At(responseParameters, 6, (ushort)eligible.Count);
            responseData = new byte[entries.Count + strings.Count];
            entries.CopyTo(responseData, 0);
            for (int i = 0; i < strings.Count; i++)
                responseData[entries.Count + i] = strings[i];
        }

        private static void GetErrorResponse(ushort win32Error, out byte[] responseParameters, out byte[] responseData)
        {
            responseParameters = new byte[8];
            WriteU16At(responseParameters, 0, win32Error);
            WriteU16At(responseParameters, 2, 0);
            WriteU16At(responseParameters, 4, 0);
            WriteU16At(responseParameters, 6, 0);
            responseData = new byte[0];
        }

        /// <returns>null when valid; "level" for bad level, "param" otherwise.</returns>
        private static string ValidateNetShareEnumRequest(byte[] request, out ushort level, out ushort receiveBufferSize)
        {
            level = 0;
            receiveBufferSize = 0;
            if (request == null || request.Length < 2)
                return "param";
            int offset = 0;
            ushort opcode = ReadU16(request, ref offset);
            if (opcode != RapOpNetShareEnum)
                return "param";
            string paramDesc = ReadCString(request, ref offset);
            if (paramDesc == null || paramDesc != "WrLeh")
                return "param";
            string dataDesc = ReadCString(request, ref offset);
            if (dataDesc == null)
                return "param";
            if (offset + 4 > request.Length)
                return "param";
            level = ReadU16(request, ref offset);
            receiveBufferSize = ReadU16(request, ref offset);
            if (level != 0 && level != 1)
                return "level";
            if (level == 0 && dataDesc != "B13")
                return "param";
            if (level == 1 && dataDesc != "B13BWz")
                return "param";
            return null;
        }

        private static void WriteName13(List<byte> output, byte[] nameBytes)
        {
            for (int i = 0; i < 13; i++)
                output.Add(i < nameBytes.Length ? nameBytes[i] : (byte)0);
        }

        private static ushort ReadU16(byte[] buffer, ref int offset)
        {
            ushort value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static string ReadCString(byte[] buffer, ref int offset)
        {
            int end = offset;
            while (end < buffer.Length && buffer[end] != 0)
                end++;
            if (end >= buffer.Length)
                return null;
            string value = Encoding.ASCII.GetString(buffer, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private static void WriteU16(List<byte> output, ushort value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
        }

        private static void WriteU16At(byte[] output, int offset, ushort value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
