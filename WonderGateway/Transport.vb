Imports System.IO
Imports System.IO.Ports

' Byte-stream transport for the WonderSwan <-> adapter serial line.
Public Interface IByteTransport
    ' Reads one byte; returns 0-255, or -1 at end-of-stream.
    Function ReadByte() As Integer
    Sub WriteByte(b As Byte)
    Sub WriteBytes(data As Byte())
    Sub Flush()
    Sub Close()
End Interface

' stdin/stdout transport (Mednafen excomm). Only protocol bytes may go to stdout.
Public Class StdioTransport
    Implements IByteTransport

    Private ReadOnly _in As Stream
    Private ReadOnly _out As Stream

    Public Sub New()
        _in = Console.OpenStandardInput()
        _out = Console.OpenStandardOutput()
    End Sub

    Public Function ReadByte() As Integer Implements IByteTransport.ReadByte
        Return _in.ReadByte()
    End Function

    Public Sub WriteByte(b As Byte) Implements IByteTransport.WriteByte
        _out.WriteByte(b)
    End Sub

    Public Sub WriteBytes(data As Byte()) Implements IByteTransport.WriteBytes
        _out.Write(data, 0, data.Length)
    End Sub

    Public Sub Flush() Implements IByteTransport.Flush
        _out.Flush()
    End Sub

    Public Sub Close() Implements IByteTransport.Close
        ' Don't close the process standard streams.
    End Sub
End Class

' Real RS-232 transport (9600/8-N-1) for hardware; selected with transport=serial.
Public Class SerialTransport
    Implements IByteTransport

    Private ReadOnly _port As SerialPort

    Public Sub New(portName As String, Optional baud As Integer = 9600)
        _port = New SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        _port.ReadTimeout = SerialPort.InfiniteTimeout
        _port.WriteTimeout = SerialPort.InfiniteTimeout
        _port.DtrEnable = True
        _port.RtsEnable = True
        _port.Open()
    End Sub

    Public Function ReadByte() As Integer Implements IByteTransport.ReadByte
        Try
            Return _port.ReadByte()
        Catch ex As TimeoutException
            Return -1
        End Try
    End Function

    Public Sub WriteByte(b As Byte) Implements IByteTransport.WriteByte
        _port.Write(New Byte() {b}, 0, 1)
    End Sub

    Public Sub WriteBytes(data As Byte()) Implements IByteTransport.WriteBytes
        _port.Write(data, 0, data.Length)
    End Sub

    Public Sub Flush() Implements IByteTransport.Flush
        _port.BaseStream.Flush()
    End Sub

    Public Sub Close() Implements IByteTransport.Close
        If _port.IsOpen Then _port.Close()
    End Sub
End Class

' In-memory transport for --selftest: feed a request, read back the reply.
Public Class MemoryTransport
    Implements IByteTransport

    Private ReadOnly _in As Queue(Of Byte)
    Public ReadOnly Output As New List(Of Byte)()

    Public Sub New(input As Byte())
        _in = New Queue(Of Byte)(input)
    End Sub

    Public Function ReadByte() As Integer Implements IByteTransport.ReadByte
        If _in.Count = 0 Then Return -1
        Return CInt(_in.Dequeue())
    End Function

    Public Sub WriteByte(b As Byte) Implements IByteTransport.WriteByte
        Output.Add(b)
    End Sub

    Public Sub WriteBytes(data As Byte()) Implements IByteTransport.WriteBytes
        Output.AddRange(data)
    End Sub

    Public Sub Flush() Implements IByteTransport.Flush
    End Sub

    Public Sub Close() Implements IByteTransport.Close
    End Sub
End Class
