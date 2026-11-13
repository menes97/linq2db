using System;
using System.Linq;

using LinqToDB.Remote;

using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

using Tests.Remote;

namespace Tests
{
	/// <summary>
	/// Marks a test whose remote cases are rejected by design when remote test hosts run untrusted
	/// (the default; <c>LINQ2DB_TESTS_TRUSTED_REMOTE=1</c> turns it off): it sends text or types the library cannot vouch for.
	/// In that mode a remote case passes only if the host recorded a rejection with the expected
	/// <see cref="Reason"/> and <see cref="Kind"/>, and fails if the query was accepted, so a stale mark is reported.
	/// Outside that mode, and for direct cases, the attribute does nothing.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
	public sealed class RejectedWhenUntrustedAttribute : NUnitAttribute, IWrapSetUpTearDown
	{
		/// <summary>Expects a rejection that carries no text kind, such as <see cref="RemoteRejectionReason.TypeNotAllowed"/>.</summary>
		public RejectedWhenUntrustedAttribute(RemoteRejectionReason reason)
		{
			Reason = reason;
		}

		/// <summary>Expects a rejection of text of <paramref name="kind"/>.</summary>
		public RejectedWhenUntrustedAttribute(RemoteRejectionReason reason, SqlTextKind kind)
		{
			Reason = reason;
			Kind   = kind;
		}

		public RemoteRejectionReason Reason { get; }
		public SqlTextKind?          Kind   { get; }

		/// <summary>Restricts the expectation to remote cases of contexts containing this text, e.g. a provider name.</summary>
		public string? Context { get; set; }

		/// <summary>
		/// Only some cases of the test are rejected (those whose arguments send the text): an accepted case passes as usual, a
		/// rejected one must still match. A stale mark is then not reported, so use it only where the cases differ.
		/// </summary>
		public bool Partial { get; set; }

		public TestCommand Wrap(TestCommand command) => new RejectedWhenUntrustedCommand(command, this);

		bool Matches(RemoteRejection rejection) => rejection.Reason == Reason && rejection.Kind == Kind;

		// the provider whose rejections the test reads, or null when the attribute does not govern the test
		string? GovernedProvider(ITest test)
		{
			if (!TestLinqService.IsUntrusted)
				return null;

			var context = test.Arguments.OfType<string>().FirstOrDefault(static a => a.EndsWith(TestBase.LinqServiceSuffix, StringComparison.Ordinal));

			return context != null && (Context == null || context.Contains(Context)) ? context.StripRemote() : null;
		}

		sealed class RejectedWhenUntrustedCommand : DelegatingTestCommand
		{
			readonly RejectedWhenUntrustedAttribute _attribute;

			public RejectedWhenUntrustedCommand(TestCommand innerCommand, RejectedWhenUntrustedAttribute attribute)
				: base(innerCommand)
			{
				_attribute = attribute;
			}

			public override TestResult Execute(TestExecutionContext context)
			{
				if (_attribute.GovernedProvider(context.CurrentTest) is not { } provider)
					return innerCommand.Execute(context);

				// Same deferral as ThrowsWhenAttribute: the heartbeat must count the rewritten result, not the raw one.
				var fullName = context.CurrentTest.FullName;

				TestProgressTracker.BeginDeferred(fullName);

				TestResult? testResult = null;

				try
				{
					TestLinqService.ClearRejections(provider);

					testResult = innerCommand.Execute(context);

					var rejections = TestLinqService.GetRejections(provider);
					var expected   = $"{_attribute.Reason}{(_attribute.Kind is { } kind ? $" [{kind}]" : null)}";

					if (rejections.Any(_attribute.Matches))
					{
						// A fresh result: the failed one keeps the rejection as an assertion result (a read-only
						// collection), which NUnit re-reports as the outcome when [Repeat] completes the test.
						var passed = context.CurrentTest.MakeTestResult();

						passed.StartTime = testResult.StartTime;
						passed.EndTime   = testResult.EndTime;
						passed.Duration  = testResult.Duration;
						passed.SetResult(ResultState.Success, $"Rejected as expected for an untrusted client: {expected}");

						context.CurrentResult = testResult = passed;
					}
					else if (rejections.Length == 0 && _attribute.Partial)
						return testResult;
					else if (rejections.Length == 0)
						testResult.SetResult(ResultState.Failure, $"Expected an untrusted-client rejection {expected}, but the request was accepted. Remove the stale [{nameof(RejectedWhenUntrustedAttribute)}].\n\n{testResult.Message}");
					else
						testResult.SetResult(ResultState.Failure, $"Expected an untrusted-client rejection {expected}, but got: {string.Join("; ", rejections.Select(static r => $"{r.Reason} [{r.Kind}] '{r.Text}'"))}");

					return testResult;
				}
				finally
				{
					TestProgressTracker.CommitDeferred(fullName, testResult);
				}
			}
		}
	}
}
