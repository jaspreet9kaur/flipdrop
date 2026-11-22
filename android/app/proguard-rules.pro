# FlipDrop R8 / ProGuard rules.
#
# Compose, AndroidX and kotlinx-coroutines ship their own consumer rules, so almost
# nothing extra is needed. We only keep what would otherwise break reflection.

# Readable crash reports: keep line numbers but still rename everything else.
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile

# FileKind.valueOf(...) / Enum.name are used when mapping to the protocol "kind" string.
-keepclassmembers enum * {
    public static **[] values();
    public static ** valueOf(java.lang.String);
}

# org.json is part of the Android platform; no rule needed. Nothing in this app is
# referenced purely by name from outside, so no other -keep entries are required.
