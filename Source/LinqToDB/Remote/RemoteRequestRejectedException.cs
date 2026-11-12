using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Thrown when <see cref="LinqService"/> rejects a request from an untrusted client.
	/// </summary>
	/// <remarks>
	/// <see cref="Exception.Message"/> may be forwarded to clients by a transport, so it contains only text the client sent,
	/// escaped and truncated, and says nothing about the catalog: a text not in the catalog, removed from it or allowed for
	/// another kind is reported alike. <see cref="Rejections"/> carries the exact reason and remedy for the host's log.
	/// Add a catalog entry or a mapping assembly only when the rejected text or type comes from your own code.
	/// </remarks>
	public sealed class RemoteRequestRejectedException : LinqToDBException
	{
		const int    MaxTextLength = 512;
		const string TextRemedy    = "The service does not accept this text from clients; the host log has the reason.";

		/// <summary>Creates the exception.</summary>
		/// <param name="rejections">All violations found, up to the reporting limit.</param>
		/// <param name="isTruncated">Whether more violations existed than were reported.</param>
		/// <param name="provider">Name of the active data provider.</param>
		/// <param name="configuration">Connection configuration requested by the client.</param>
		/// <param name="innerException">Failure caused by continuing after the first violation, if any.</param>
		public RemoteRequestRejectedException(
			IReadOnlyList<RemoteRejection> rejections,
			bool                           isTruncated,
			string?                        provider,
			string?                        configuration,
			Exception?                     innerException = null)
			: base(BuildMessage(rejections, isTruncated, provider, configuration), innerException!)
		{
			Rejections    = rejections;
			IsTruncated   = isTruncated;
			Provider      = provider;
			Configuration = configuration;
		}

		/// <summary>Gets all violations found, up to the reporting limit.</summary>
		public IReadOnlyList<RemoteRejection> Rejections { get; }
		/// <summary>Gets a value indicating whether more violations existed than were reported.</summary>
		public bool IsTruncated { get; }
		/// <summary>Gets the name of the active data provider.</summary>
		public string? Provider { get; }
		/// <summary>Gets the connection configuration requested by the client.</summary>
		public string? Configuration { get; }

		static string BuildMessage(IReadOnlyList<RemoteRejection> rejections, bool isTruncated, string? provider, string? configuration)
		{
			var sb = new StringBuilder();

			// the configuration is client text too
			sb.Append(CultureInfo.InvariantCulture, $"Request rejected by the service policy (provider '{Escape(provider ?? "")}', configuration '{Escape(configuration ?? "")}').");

			foreach (var rejection in rejections)
			{
				// which catalog texts the host removed or allows elsewhere is the host's business: one reason and remedy for all three
				var isText = rejection.Reason is RemoteRejectionReason.TextNotInCatalog or RemoteRejectionReason.TextRemovedFromCatalog or RemoteRejectionReason.TextAllowedForOtherKind;

				sb.AppendLine();
				sb.Append(isText ? "TextNotAccepted" : rejection.Reason.ToString());

				if (rejection.Kind != null)
					sb.Append(CultureInfo.InvariantCulture, $" [{rejection.Kind}]");

				if (rejection.NodeType != null)
					sb.Append(CultureInfo.InvariantCulture, $" at {Escape(rejection.NodeType)}");

				sb.Append(CultureInfo.InvariantCulture, $": '{Escape(rejection.Text)}'. {(isText ? TextRemedy : rejection.Remedy)}");
			}

			if (isTruncated)
			{
				sb.AppendLine();
				sb.Append("More violations were not reported.");
			}

			return sb.ToString();
		}

		static string Escape(string text)
		{
			var sb = new StringBuilder(Math.Min(text.Length, MaxTextLength));

			for (var i = 0; i < text.Length && i < MaxTextLength; i++)
			{
				var c = text[i];

				// format characters (bidi overrides) and line separators spoof or split a log line as control characters do
				if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
					sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
				else
					sb.Append(c);
			}

			if (text.Length > MaxTextLength)
				sb.Append("...");

			return sb.ToString();
		}
	}
}
