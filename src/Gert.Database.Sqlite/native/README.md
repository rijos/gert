# Vendored native extension

`vec0.so` is the official prebuilt sqlite-vec **v0.1.9** loadable extension
(linux-x86_64), from https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9.
Verified: loads via Microsoft.Data.Sqlite, `vec_version()` = v0.1.9, KNN works.
Copied to build output as `vec0.so`; the provider loads it on each rag.db connection.
Add mac/win variants here when those targets are needed.
