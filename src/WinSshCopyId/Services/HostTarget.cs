using System;
using System.Collections.Generic;

namespace WinSshCopyId.Services;

/// <summary>A single host to deploy to: a name/IP and a port.</summary>
public sealed class HostTarget
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    public string Display => Port == 22 ? Host : $"{Host}:{Port}";

    /// <summary>
    /// Parse one host per line. Each line may be "host", "host:port", or
    /// "host port". Blank lines and lines starting with '#' are ignored.
    /// Invalid ports fall back to <paramref name="defaultPort"/>.
    /// </summary>
    public static IReadOnlyList<HostTarget> ParseList(string text, int defaultPort)
    {
        if (defaultPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(defaultPort));
        var list = new List<HostTarget>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            HostTarget? target = ParseLine(line, defaultPort);
            if (target is not null)
            {
                SshInput.ValidateEndpoint(target.Host, target.Port);
                list.Add(target);
            }
        }
        return list;
    }

    private static HostTarget? ParseLine(string line, int defaultPort)
    {
        string host;
        int port = defaultPort;

        if (line.StartsWith('['))
        {
            // Bracketed IPv6: "[2001:db8::1]" or "[2001:db8::1]:2222".
            int close = line.IndexOf(']');
            if (close <= 1)
            {
                throw new FormatException("Invalid bracketed host.");
            }
            host = line[1..close];
            string rest = line[(close + 1)..].Trim();
            if (rest.StartsWith(':'))
            {
                if (!int.TryParse(rest[1..].Trim(), out port)) throw new FormatException("Invalid SSH port.");
            }
            else if (rest.Length != 0) throw new FormatException("Unexpected host suffix.");
        }
        else
        {
            // Split off an optional "port" written after whitespace.
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 2) throw new FormatException("Unexpected host fields.");
            if (tokens.Length == 2 && !int.TryParse(tokens[1], out _)) throw new FormatException("Invalid SSH port.");
            string first = tokens[0];
            if (tokens.Length >= 2 && int.TryParse(tokens[1], out int spacePort))
            {
                port = spacePort;
                host = first;
            }
            else if (CountColons(first) == 1)
            {
                // host:port for IPv4 / hostnames (a single colon only).
                int idx = first.IndexOf(':');
                if (int.TryParse(first[(idx + 1)..], out int colonPort))
                {
                    host = first[..idx];
                    port = colonPort;
                }
                else
                {
                    throw new FormatException("Invalid SSH port.");
                }
            }
            else
            {
                // Bare hostname, IPv4, or unbracketed IPv6 literal.
                host = first;
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new FormatException("SSH port must be between 1 and 65535.");
        }
        return host.Length == 0 ? null : new HostTarget { Host = host, Port = port };
    }

    private static int CountColons(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (c == ':')
            {
                n++;
            }
        }
        return n;
    }
}
