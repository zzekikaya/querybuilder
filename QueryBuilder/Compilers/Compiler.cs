using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SqlKata.Compilers
{
    public partial class Compiler : AbstractCompiler
    {

        public Compiler() : base()
        {
            Inflector = new Inflector();
        }

        protected override string OpeningIdentifier()
        {
            return "\"";
        }

        protected override string ClosingIdentifier()
        {
            return "\"";
        }

        public SqlResult Compile(Query query)
        {
            query = OnBeforeCompile(query);

            string sql;

            if (query.Method == "insert")
            {
                sql = CompileInsert(query);
            }
            else if (query.Method == "delete")
            {
                sql = CompileDelete(query);
            }
            else if (query.Method == "update")
            {
                sql = CompileUpdate(query);
            }
            else
            {
                sql = CompileSelect(query);
            }

            if (query.Get("cte", EngineCode).Any())
            {
                sql = CompileCte(query) + sql;
            }

            // filter out foreign clauses so we get the bindings
            // just for the current engine
            var bindings = query.GetBindings(EngineCode);

            sql = OnAfterCompile(sql, bindings);
            return new SqlResult(sql, bindings);
        }

        protected virtual Query OnBeforeCompile(Query query)
        {
            return query;
        }

        public virtual string OnAfterCompile(string sql, List<object> bindings)
        {
            return sql;
        }

        public virtual string CompileCte(Query query)
        {
            var clauses = query.Get<AbstractFrom>("cte", EngineCode);

            if (!clauses.Any())
            {
                return "";
            }

            var sql = new List<string>();

            foreach (var cte in clauses)
            {
                if (cte is RawFromClause)
                {
                    RawFromClause clause = (cte as RawFromClause);
                    sql.Add($"{WrapValue(clause.Alias)} AS ({WrapIdentifiers(clause.Expression)})");
                }
                else if (cte is QueryFromClause)
                {
                    QueryFromClause clause = (cte as QueryFromClause);
                    sql.Add($"{WrapValue(clause.Alias)} AS ({CompileSelect(clause.Query)})");
                }
            }

            return "WITH " + string.Join(", ", sql) + " ";
        }


        public virtual string CompileSelect(Query query)
        {
            query = OnBeforeSelect(query);

            if (!query.Has("select", EngineCode))
            {
                query.Select("*");
            }

            var results = CompileComponents(query);

            return JoinComponents(results, "select");
        }

        protected virtual Query OnBeforeSelect(Query query)
        {
            return query;
        }

        /// <summary>
        /// Compile INSERT into statement
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        protected virtual string CompileInsert(Query query)
        {
            if (!query.Has("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to insert");
            }

            var from = query.GetOne<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            var insert = query.GetOne<AbstractInsertClause>("insert", EngineCode);


            if (insert is InsertClause)
            {
                var clause = insert as InsertClause;

                return "INSERT INTO " + CompileTableExpression(from)
                + " (" + string.Join(", ", WrapArray(clause.Columns)) + ") "
                + "VALUES (" + string.Join(", ", Parametrize(clause.Values)) + ")";
            }
            else
            {
                var clause = insert as InsertQueryClause;
                return "INSERT INTO " + CompileTableExpression(from)
                + " " + CompileSelect(clause.Query);
            }

        }


        protected virtual string CompileUpdate(Query query)
        {
            if (!query.Has("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to update");
            }

            var from = query.GetOne<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            var toUpdate = query.GetOne<InsertClause>("update", EngineCode);

            var sql = new List<string>();

            for (var i = 0; i < toUpdate.Columns.Count; i++)
            {
                sql.Add($"{Wrap(toUpdate.Columns[i])} = ?");
            }

            var where = CompileWheres(query);

            if (!string.IsNullOrEmpty(where))
            {
                where = " " + where;
            }

            return "UPDATE " + CompileTableExpression(from)
                + " SET " + string.Join(", ", sql)
                + where;
        }

        protected virtual string CompileDelete(Query query)
        {
            if (!query.Has("from", EngineCode))
            {
                throw new InvalidOperationException("No table set to delete");
            }

            var from = query.GetOne<AbstractFrom>("from", EngineCode);

            if (!(from is FromClause))
            {
                throw new InvalidOperationException("Invalid table expression");
            }

            var where = CompileWheres(query);

            if (!string.IsNullOrEmpty(where))
            {
                where = " " + where;
            }

            return "DELETE FROM " + CompileTableExpression(from) + where;
        }

        protected List<string> CompileComponents(Query query)
        {
            var result = (new List<string>
            {
                this.CompileAggregate(query),
                this.CompileColumns(query),
                this.CompileFrom(query),
                this.CompileJoins(query),
                this.CompileWheres(query),
                this.CompileGroups(query),
                this.CompileHavings(query),
                this.CompileOrders(query),
                this.CompileLimit(query),
                this.CompileOffset(query),
                this.CompileUnions(query),
                this.CompileLock(query),
            })
            .ToList()
            .Where(x => x != null)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

            return result;
        }


        protected virtual string CompileColumns(Query query)
        {
            // If the query is actually performing an aggregating select, we will let that
            // compiler handle the building of the select clauses, as it will need some
            // more syntax that is best handled by that function to keep things neat.
            if (query.Has("aggregate", EngineCode))
            {
                return null;
            }

            if (!query.Has("select", EngineCode))
            {
                return null;
            }

            var columns = query.Get("select", EngineCode).Cast<AbstractColumn>().ToList();

            var select = (query.IsDistinct ? "SELECT DISTINCT " : "SELECT ");
            return select + (columns.Any() ? Columnize(columns) : "*");
        }

        protected virtual string CompileAggregate(Query query)
        {

            if (!query.Has("aggregate", EngineCode))
            {
                return null;
            }

            var ag = query.Get("aggregate").Cast<AggregateClause>().First();

            var cols = ag.Columns
                .Select(x => new Column { Name = x })
                .Cast<AbstractColumn>()
                .ToList();

            var columns = Columnize(cols);

            if (query.IsDistinct && columns != "*")
            {
                columns = "DISTINCT " + columns;
            }

            return "SELECT " + ag.Type.ToUpper() + "(" + columns + ") AS " + Wrap("count");
        }

        protected virtual string CompileTableExpression(AbstractFrom from)
        {
            if (from is RawFromClause)
            {
                return WrapIdentifiers((from as RawFromClause).Expression);
            }

            if (from is QueryFromClause)
            {
                var fromQuery = (from as QueryFromClause).Query;

                var alias = string.IsNullOrEmpty(fromQuery.QueryAlias) ? "" : " AS " + WrapValue(fromQuery.QueryAlias);

                var compiled = CompileSelect(fromQuery);

                return "(" + compiled + ")" + alias;
            }

            if (from is FromClause)
            {
                return WrapTable((from as FromClause).Table);
            }

            throw InvalidClauseException("TableExpression", from);
        }

        protected virtual string CompileFrom(Query query)
        {
            if (!query.Has("from", EngineCode))
            {
                return null;
            }

            var from = query.GetOne<AbstractFrom>("from", EngineCode);

            return "FROM " + CompileTableExpression(from);
        }

        protected virtual string CompileJoins(Query query)
        {
            if (!query.Has("join", EngineCode))
            {
                return null;
            }

            // Transfrom deep join expressions to regular join

            var deepJoins = query.Get<AbstractJoin>("join", EngineCode).OfType<DeepJoin>().ToList();

            foreach (var deepJoin in deepJoins)
            {
                var index = query.Clauses.IndexOf(deepJoin);

                query.Clauses.Remove(deepJoin);
                foreach (var join in TransfromDeepJoin(query, deepJoin))
                {
                    query.Clauses.Insert(index, join);
                    index++;
                }
            }

            var joins = query.Get<BaseJoin>("join", EngineCode);

            var sql = new List<string>();

            foreach (var item in joins)
            {
                sql.Add(CompileJoin(item.Join));
            }

            return JoinComponents(sql, "join");
        }

        protected virtual string CompileJoin(Join join, bool isNested = false)
        {

            var from = join.GetOne<AbstractFrom>("from", EngineCode);
            var conditions = join.Get<AbstractCondition>("where", EngineCode);

            var joinTable = CompileTableExpression(from);
            var constraints = CompileConditions(conditions);

            var onClause = conditions.Any() ? $" ON {constraints}" : "";

            return $"{join.Type} JOIN {joinTable}{onClause}";
        }

        protected virtual string CompileWheres(Query query)
        {
            if (!query.Has("from", EngineCode) || !query.Has("where", EngineCode))
            {
                return null;
            }

            var conditions = query.Get<AbstractCondition>("where", EngineCode);
            var sql = CompileConditions(conditions);

            return $"WHERE {sql}";
        }

        protected string CompileQuery<T>(
                BaseQuery<T> query,
                string joinType = "",
                bool isNested = false
        ) where T : BaseQuery<T>
        {
            if (query is Query)
            {
                return CompileSelect(query as Query);
            }

            if (query is Join)
            {
                return CompileJoin((query as Join), isNested);
            }

            return "";
        }

        protected virtual string CompileGroups(Query query)
        {
            if (!query.Has("group", EngineCode))
            {
                return null;
            }

            var cols = query.Get("group", EngineCode)
                .Select(x => x as AbstractColumn)
                .ToList();

            return "GROUP BY " + Columnize(cols);
        }

        protected virtual string CompileOrders(Query query)
        {
            if (!query.Has("order", EngineCode))
            {
                return null;
            }

            var columns = query.Get<AbstractOrderBy>("order", EngineCode).Select(x =>
            {

                if (x is RawOrderBy)
                {
                    return WrapIdentifiers((x as RawOrderBy).Expression);
                }

                var direction = (x as OrderBy).Ascending ? "" : "DESC";

                return Wrap((x as OrderBy).Column) + " " + direction;
            });

            return "ORDER BY " + string.Join(", ", columns);
        }

        public string CompileHavings(Query query)
        {
            if (!query.Has("having", EngineCode))
            {
                return null;
            }

            var sql = new List<string>();
            string boolOperator;

            var havings = query.Get("having", EngineCode)
                .Cast<AbstractCondition>()
                .ToList();

            for (var i = 0; i < havings.Count; i++)
            {
                var compiled = CompileCondition(havings[i]);

                if (!string.IsNullOrEmpty(compiled))
                {
                    boolOperator = i > 0 ? havings[i].IsOr ? "OR " : "AND " : "";

                    sql.Add(boolOperator + "HAVING " + compiled);
                }
            }

            return JoinComponents(sql, "having");
        }

        public virtual string CompileLimit(Query query)
        {
            var limitOffset = query.GetOne("limit", EngineCode) as LimitOffset;

            if (limitOffset != null && limitOffset.HasLimit())
            {
                return "LIMIT ?";
            }

            return "";
        }

        public virtual string CompileOffset(Query query)
        {
            var limitOffset = query.GetOne("limit", EngineCode) as LimitOffset;

            if (limitOffset != null && limitOffset.HasOffset())
            {
                return "OFFSET ?";
            }

            return "";
        }

        protected virtual string CompileUnions(Query query)
        {
            // throw new NotImplementedException();
            return null;
        }

        protected virtual string CompileLock(Query query)
        {
            // throw new NotImplementedException();
            return null;
        }

        /// <summary>
        /// Compile the random statement into SQL.
        /// </summary>
        /// <param name="seed"></param>
        /// <returns></returns>
        public virtual string CompileRandom(string seed)
        {
            return "RANDOM()";
        }

        public virtual string CompileLower(string value)
        {
            return $"LOWER({value})";
        }

        public virtual string CompileUpper(string value)
        {
            return $"UPPER({value})";
        }

        private InvalidCastException InvalidClauseException(string section, AbstractClause clause)
        {
            return new InvalidCastException($"Invalid type \"{clause.GetType().Name}\" provided for the \"{section}\" clause.");
        }

        private string Capitalize(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return str;
            }

            return str.Substring(0, 1).ToUpper() + str.Substring(1).ToLower();
        }

        protected string DynamicCompile(string name, AbstractClause clause)
        {

            MethodInfo methodInfo = this.GetType()
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

            if (methodInfo == null)
            {
                throw new Exception($"Failed to locate a compiler for {name}.");
            }

            var isGeneric = clause.GetType()
#if FEATURE_TYPE_INFO
            .GetTypeInfo()
#endif
            .IsGenericType;

            if (isGeneric && methodInfo.GetGenericArguments().Any())
            {
                var args = clause.GetType().GetGenericArguments();
                methodInfo = methodInfo.MakeGenericMethod(args);
            }

            var result = methodInfo.Invoke(this, new object[] { clause });

            return result as string;
        }

        protected virtual IEnumerable<BaseJoin> TransfromDeepJoin(Query query, DeepJoin join)
        {
            var exp = join.Expression;

            var tokens = exp.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            if (!tokens.Any())
            {
                yield break;
            }


            var from = query.GetOne<AbstractFrom>("from", EngineCode);

            if (from == null)
            {
                yield break;
            }

            string tableOrAlias = from.Alias;

            if (string.IsNullOrEmpty(tableOrAlias))
            {
                throw new InvalidOperationException("No table or alias found for the main query, This information is needed in order to generate a Deep Join");
            }

            for (var i = 0; i < tokens.Length; i++)
            {
                var source = i == 0 ? tableOrAlias : tokens[i - 1];
                var target = tokens[i];

                string sourceKey;
                string targetKey;

                if (join.SourceKeyGenerator != null)
                {
                    // developer wants to use the lambda overloaded method then
                    sourceKey = join.SourceKeyGenerator.Invoke(target);
                    targetKey = join.TargetKeyGenerator?.Invoke(target) ?? "Id";
                }
                else
                {
                    sourceKey = Singular(target) + join.SourceKeySuffix;
                    targetKey = join.TargetKey;
                }

                // yield query.Join(target, $"{source}.{sourceKey}", $"{target}.{targetKey}", "=", join.Type);
                yield return new BaseJoin
                {
                    Component = "join",
                    Join = new Join().AsType(join.Type).JoinWith(target).On
                    ($"{source}.{sourceKey}", $"{target}.{targetKey}", "=")
                };
            }

        }

    }



}