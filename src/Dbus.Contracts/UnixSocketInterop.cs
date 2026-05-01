using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Dbus.Contracts;

internal static unsafe class UnixSocketInterop
{
    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;

    public static int Send(Socket socket, byte[] payload, int[] fileDescriptors)
    {
        if (fileDescriptors.Length == 0)
        {
            var sent = 0;
            while (sent < payload.Length)
            {
                sent += socket.Send(payload.AsSpan(sent), SocketFlags.None);
            }

            return sent;
        }

        fixed (byte* payloadPointer = payload)
        fixed (int* descriptorPointer = fileDescriptors)
        {
            var iov = new IOVec
            {
                Base = (IntPtr)payloadPointer,
                Length = (UIntPtr)payload.Length
            };

            var descriptorBytes = checked(fileDescriptors.Length * sizeof(int));
            var controlLength = CmsgSpace(descriptorBytes);
            var control = stackalloc byte[controlLength];
            new Span<byte>(control, controlLength).Clear();

            var header = (Cmsghdr*)control;
            header->Length = (UIntPtr)CmsgLen(descriptorBytes);
            header->Level = SOL_SOCKET;
            header->Type = SCM_RIGHTS;
            Buffer.MemoryCopy(descriptorPointer, control + CmsgHeaderLength(), descriptorBytes, descriptorBytes);

            var message = new Msghdr
            {
                Iov = (IntPtr)(&iov),
                IovLength = (UIntPtr)1,
                Control = (IntPtr)control,
                ControlLength = (UIntPtr)controlLength
            };

            var sent = sendmsg((int)socket.Handle, &message, 0);
            if (sent < 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            if (sent != payload.Length)
            {
                throw new DbusException("D-Bus unix-fd message was only partially sent.");
            }

            return sent;
        }
    }

    public static (int BytesReceived, int[] FileDescriptors) Receive(Socket socket, byte[] buffer)
    {
        fixed (byte* bufferPointer = buffer)
        {
            var iov = new IOVec
            {
                Base = (IntPtr)bufferPointer,
                Length = (UIntPtr)buffer.Length
            };

            var controlLength = CmsgSpace(sizeof(int) * 32);
            var control = stackalloc byte[controlLength];
            new Span<byte>(control, controlLength).Clear();

            var message = new Msghdr
            {
                Iov = (IntPtr)(&iov),
                IovLength = (UIntPtr)1,
                Control = (IntPtr)control,
                ControlLength = (UIntPtr)controlLength
            };

            var received = recvmsg((int)socket.Handle, &message, 0);
            if (received < 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            var descriptors = ReadFileDescriptors(control, (int)message.ControlLength);
            return (received, descriptors);
        }
    }

    private static int[] ReadFileDescriptors(byte* control, int controlLength)
    {
        var descriptors = new List<int>();
        var offset = 0;
        while (offset + CmsgHeaderLength() <= controlLength)
        {
            var header = (Cmsghdr*)(control + offset);
            var length = (int)header->Length;
            if (length < CmsgHeaderLength() || offset + length > controlLength)
            {
                break;
            }

            if (header->Level == SOL_SOCKET && header->Type == SCM_RIGHTS)
            {
                var dataLength = length - CmsgHeaderLength();
                var descriptorCount = dataLength / sizeof(int);
                var data = (int*)(control + offset + CmsgHeaderLength());
                for (var i = 0; i < descriptorCount; i++)
                {
                    descriptors.Add(data[i]);
                }
            }

            offset += CmsgSpace(length - CmsgHeaderLength());
        }

        return descriptors.ToArray();
    }

    private static int CmsgHeaderLength()
    {
        return Align(sizeof(nuint) + sizeof(int) + sizeof(int));
    }

    private static int CmsgLen(int dataLength)
    {
        return CmsgHeaderLength() + dataLength;
    }

    private static int CmsgSpace(int dataLength)
    {
        return Align(CmsgHeaderLength() + dataLength);
    }

    private static int Align(int value)
    {
        var alignment = sizeof(nuint);
        return (value + alignment - 1) & ~(alignment - 1);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sendmsg(int sockfd, Msghdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int recvmsg(int sockfd, Msghdr* msg, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct IOVec
    {
        public IntPtr Base;

        public UIntPtr Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msghdr
    {
        public IntPtr Name;

        public uint NameLength;

        public IntPtr Iov;

        public UIntPtr IovLength;

        public IntPtr Control;

        public UIntPtr ControlLength;

        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Cmsghdr
    {
        public UIntPtr Length;

        public int Level;

        public int Type;
    }
}
