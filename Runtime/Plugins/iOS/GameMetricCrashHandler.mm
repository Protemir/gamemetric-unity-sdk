// iOS native crash writer (managed layer): installs an uncaught NSException
// handler that catches uncaught Objective-C exceptions Unity's managed hook can't
// see, and writes a crash record matching the SDK's on-disk contract:
//   {persistentDataPath}/gamemetric/native-crashes/ios-{millis}.json
// The managed SDK picks it up, emits it, and deletes it on the next launch.
//
// This runs with the runtime still up (it's an NSException, not a signal), so
// Foundation calls and file I/O are safe here. We chain to any previously-installed
// handler. Global config is kept as C strings (strdup'd), so this file is safe to
// compile with or without ARC.
//
// NOTE: NSSetUncaughtExceptionHandler covers uncaught Objective-C exceptions only —
// the analog of Android's Java handler. True native signals (SIGSEGV / Mach
// exceptions in il2cpp / C/C++) need a separate signal-based handler (iOS Phase 2).

#import <Foundation/Foundation.h>

static char *gm_crashDir = NULL;
static char *gm_buildId = NULL;
static NSUncaughtExceptionHandler *gm_previousHandler = NULL;

static void gm_writeRecord(NSException *exception) {
    @try {
        if (gm_crashDir == NULL) {
            return;
        }

        NSString *dir = [NSString stringWithUTF8String:gm_crashDir];
        NSString *buildId = gm_buildId != NULL ? [NSString stringWithUTF8String:gm_buildId] : @"";

        [[NSFileManager defaultManager] createDirectoryAtPath:dir
                                  withIntermediateDirectories:YES
                                                   attributes:nil
                                                        error:nil];

        NSString *name = exception.name ?: @"NSException";
        NSString *reason = exception.reason ?: name;
        NSArray *stack = [exception callStackSymbols] ?: @[];

        NSDateFormatter *fmt = [[NSDateFormatter alloc] init];
        fmt.dateFormat = @"yyyy-MM-dd'T'HH:mm:ss.SSS'Z'";
        fmt.timeZone = [NSTimeZone timeZoneWithAbbreviation:@"UTC"];
        fmt.locale = [[NSLocale alloc] initWithLocaleIdentifier:@"en_US_POSIX"];
        NSString *ts = [fmt stringFromDate:[NSDate date]];

        NSDictionary *record = @{
            @"schema": @1,
            @"platform": @"ios",
            @"type": name,
            @"message": reason,
            @"stack": stack,          // array of frame strings; the reader joins it
            @"build_id": buildId,
            @"timestamp": ts,
        };

        NSError *err = nil;
        NSData *data = [NSJSONSerialization dataWithJSONObject:record options:0 error:&err];
        if (data == nil) {
            return;
        }

        double millis = [[NSDate date] timeIntervalSince1970] * 1000.0;
        NSString *file = [dir stringByAppendingPathComponent:
            [NSString stringWithFormat:@"ios-%.0f.json", millis]];

        // atomically:YES writes to a temp file and renames, so the managed reader
        // never sees a partial file.
        [data writeToFile:file atomically:YES];
    } @catch (__unused NSException *ignored) {
        // Never let our own failure mask the original crash.
    }
}

static void gm_handler(NSException *exception) {
    gm_writeRecord(exception);
    if (gm_previousHandler != NULL) {
        gm_previousHandler(exception);
    }
}

extern "C" void gamemetric_install_crash_handler(const char *crashDir, const char *buildId) {
    @autoreleasepool {
        if (crashDir == NULL) {
            return;
        }

        if (gm_crashDir != NULL) {
            free(gm_crashDir);
        }
        if (gm_buildId != NULL) {
            free(gm_buildId);
        }
        gm_crashDir = strdup(crashDir);
        gm_buildId = strdup(buildId != NULL ? buildId : "");

        NSUncaughtExceptionHandler *current = NSGetUncaughtExceptionHandler();
        if (current == &gm_handler) {
            return; // already installed — idempotent
        }
        gm_previousHandler = current;
        NSSetUncaughtExceptionHandler(&gm_handler);
    }
}
