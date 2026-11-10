using System.Linq.Expressions;

using LinqToDB;
using LinqToDB.Internal.Expressions;
using LinqToDB.Internal.SqlQuery;

namespace LinqToDB.Internal.Linq.Builder
{
	[BuildsMethodCall(nameof(LinqExtensions.With), nameof(LinqExtensions.WithTableExpression))]
	sealed class WithTableExpressionBuilder : MethodCallBuilder
	{
		public static bool CanBuildMethod(MethodCallExpression call)
			=> call.IsQueryable;

		protected override BuildSequenceResult BuildMethodCall(ExpressionBuilder builder, MethodCallExpression methodCall, BuildInfo buildInfo)
		{
			var sequence = builder.BuildSequence(new BuildInfo(buildInfo, methodCall.Arguments[0]));
			var table    = SequenceHelper.GetTableContext(sequence) ?? throw new LinqToDBException($"Cannot get table context from {sequence.GetType()}");
			var value    = builder.EvaluateExpression<string>(methodCall.Arguments[1]);

			table.SqlTable.SqlTableType   = SqlTableType.Expression;
			table.SqlTable.TableArguments = [];

			switch (methodCall.Method.Name)
			{
				#pragma warning disable LINQ2DB0007 // user-supplied text: rejected for untrusted clients unless the host adds it
				case nameof(LinqExtensions.With)                : table.SqlTable.Expression = $"{{0}} {{1}} WITH ({value})"; break;
				case nameof(LinqExtensions.WithTableExpression) : table.SqlTable.Expression = value;                         break;
				#pragma warning restore LINQ2DB0007
			}

			return BuildSequenceResult.FromContext(sequence);
		}
	}
}
