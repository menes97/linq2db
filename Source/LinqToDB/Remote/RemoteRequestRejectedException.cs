using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LinqToDB.Remote
{
	/// <summary>
	/// Reason why <see cref="LinqService"/> rejected part of a request from an untrusted client.
	/// </summary>
	public enum RemoteRejectionReason
	{
		/// <summary>The text is not in the catalog for its kind.</summary>
		TextNotInCatalog,
		/// <summary>The text was removed from the catalog.</summary>
		TextRemovedFromCatalog,
		/// <summary>The text is in the catalog, but for a different kind.</summary>
		TextAllowedForOtherKind,
		/// <summary>A placeholder refers to an argument that does not exist.</summary>
		PlaceholderOutOfRange,
		/// <summary>An identifier neither resolves through the server mapping nor has an allowed shape.</summary>
		IdentifierNotMapped,
		/// <summary>The type is not allowed in this position.</summary>
		TypeNotAllowed,
		/// <summary>The extension builder type is not known to the server.</summary>
		BuilderNotAllowed,
		/// <summary>The connection configuration is not allowed.</summary>
		ConfigurationNotAllowed,
		/// <summary>The query element is not allowed.</summary>
		NodeNotAllowed,
		/// <summary>An enumeration value is not defined.</summary>
		InvalidEnumValue,
	}

	/// <summary>
	/// One violation found in a rejected request.
	/// </summary>
	public sealed class RemoteRejection
	{
		/// <summary>Creates a rejection.</summary>
		/// <param name="reason">Why the request was rejected.</param>
		/// <param name="kind">Text position, when the violation concerns SQL text.</param>
		/// <param name="text">Rejected text or type name, as sent by the client.</param>
		/// <param name="nodeType">Query element or role where the violation was found.</param>
		/// <param name="remedy">What the host can do when this is a false rejection.</param>
		public RemoteRejection(RemoteRejectionReason reason, SqlTextKind? kind, string text, string? nodeType, string remedy)
		{
			Reason   = reason;
			Kind     = kind;
			Text     = text;
			NodeType = nodeType;
			Remedy   = remedy;
		}

		/// <summary>Gets why the request was rejected.</summary>
		public RemoteRejectionReason Reason { get; }
		/// <summary>Gets the text position, when the violation concerns SQL text.</summary>
		public SqlTextKind? Kind { get; }
		/// <summary>Gets the rejected text or type name, as sent by the client.</summary>
		public string Text { get; }
		/// <summary>Gets the query element or role where the violation was found.</summary>
		public string? NodeType { get; }
		/// <summary>Gets what the host can do when this is a false rejection.</summary>
		public string Remedy { get; }
	}

	/// <summary>
	/// Thrown when <see cref="LinqService"/> rejects a request from an untrusted client.
	/// </summary>
	/// <remarks>
	/// <see cref="Exception.Message"/> may be forwarded to clients by a transport, so it contains only text the client sent.
	/// Add a catalog entry or a mapping assembly only when the rejected text or type comes from your own code.
	/// </remarks>
	public sealed class RemoteRequestRejectedException : LinqToDBException
	{
		const int MaxTextLength = 512;

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

			sb.Append(CultureInfo.InvariantCulture, $"Request rejected by the service policy (provider '{provider}', configuration '{configuration}').");

			foreach (var rejection in rejections)
			{
				sb.AppendLine();
				sb.Append(CultureInfo.InvariantCulture, $"{rejection.Reason}");

				if (rejection.Kind != null)
					sb.Append(CultureInfo.InvariantCulture, $" [{rejection.Kind}]");

				if (rejection.NodeType != null)
					sb.Append(CultureInfo.InvariantCulture, $" at {rejection.NodeType}");

				sb.Append(CultureInfo.InvariantCulture, $": '{Escape(rejection.Text)}'. {rejection.Remedy}");
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

				if (char.IsControl(c))
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
