// SPDX-License-Identifier: MIT
/* Kick75 IO side-LED controller for macOS. */
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/hid/IOHIDLib.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

enum {
    KICK75_VENDOR_ID = 0x19f5,
    KICK75_PRODUCT_ID = 0x1026,
    NUPHY_WRITE_COMMAND = 0x55,
    NUPHY_READ_COMMAND = 0xaa,
    NUPHY_GET_LIGHT_STATE = 0xd5,
    NUPHY_SET_LIGHT_STATE = 0xd6,
    NUPHY_SET_SECRET_KEY = 0xee,
    REPORT_SIZE = 64,
    LIGHT_STATE_SIZE = 17,
};

typedef enum {
    OP_READ,
    OP_TEST_GREEN,
    OP_COLOR,
    OP_SET_SIDE,
} Operation;

typedef struct {
    bool received;
    IOReturn result;
    CFIndex length;
    uint8_t report[REPORT_SIZE];
} InputContext;

static long number_property(IOHIDDeviceRef device, CFStringRef key) {
    CFTypeRef value = IOHIDDeviceGetProperty(device, key);
    long result = -1;
    if (value && CFGetTypeID(value) == CFNumberGetTypeID()) {
        CFNumberGetValue((CFNumberRef)value, kCFNumberLongType, &result);
    }
    return result;
}

static uint8_t checksum(const uint8_t report[REPORT_SIZE]) {
    unsigned int sum = 0;
    for (size_t i = 4; i < REPORT_SIZE; i++) sum += report[i];
    return (uint8_t)(sum & 0xff);
}

static void input_callback(
    void *context,
    IOReturn result,
    void *sender,
    IOHIDReportType type,
    uint32_t report_id,
    uint8_t *report,
    CFIndex report_length
) {
    (void)sender;
    (void)type;
    (void)report_id;
    InputContext *input = context;
    input->result = result;
    input->length = report_length > REPORT_SIZE ? REPORT_SIZE : report_length;
    memcpy(input->report, report, (size_t)input->length);
    input->received = true;
    CFRunLoopStop(CFRunLoopGetCurrent());
}

static void print_hex(const uint8_t *bytes, size_t length) {
    for (size_t i = 0; i < length; i++) {
        printf("%02x%s", bytes[i], i + 1 == length ? "" : " ");
    }
    printf("\n");
}

static bool parse_hex_byte(char high, char low, uint8_t *value) {
    char text[3] = {high, low, '\0'};
    char *end = NULL;
    long parsed = strtol(text, &end, 16);
    if (!end || *end != '\0' || parsed < 0 || parsed > 255) return false;
    *value = (uint8_t)parsed;
    return true;
}

static bool parse_side_state(const char *text, uint8_t state[8]) {
    if (strlen(text) != 16) return false;
    for (size_t i = 0; i < 8; i++) {
        if (!parse_hex_byte(text[i * 2], text[i * 2 + 1], &state[i])) return false;
    }
    return true;
}

static void print_side_state_machine(const uint8_t state[8]) {
    printf("SIDE_STATE=");
    for (size_t i = 0; i < 8; i++) printf("%02x", state[i]);
    printf("\n");
}

static bool exchange_report(
    IOHIDDeviceRef device,
    InputContext *input,
    const uint8_t report[REPORT_SIZE]
) {
    memset(input, 0, sizeof(*input));
    IOReturn write_result = IOHIDDeviceSetReport(
        device,
        kIOHIDReportTypeOutput,
        0,
        report,
        REPORT_SIZE
    );
    if (write_result != kIOReturnSuccess) {
        fprintf(stderr, "report write failed: 0x%08x\n", write_result);
        return false;
    }
    SInt32 run_result = CFRunLoopRunInMode(kCFRunLoopDefaultMode, 1.0, false);
    (void)run_result;
    if (!input->received) {
        fprintf(stderr, "timed out waiting for input report\n");
        return false;
    }
    if (input->result != kIOReturnSuccess) {
        fprintf(stderr, "input callback failed: 0x%08x\n", input->result);
        return false;
    }
    return true;
}

static bool set_light_data(
    IOHIDDeviceRef device,
    InputContext *input,
    uint8_t secret_key,
    uint16_t address,
    const uint8_t *data,
    size_t data_length
) {
    if (data_length > REPORT_SIZE - 8) return false;
    uint8_t report[REPORT_SIZE] = {0};
    report[0] = NUPHY_WRITE_COMMAND;
    report[1] = NUPHY_SET_LIGHT_STATE;
    report[2] = 0;
    report[4] = (uint8_t)data_length ^ secret_key;
    report[5] = (uint8_t)(address & 0xff) ^ secret_key;
    report[6] = (uint8_t)(address >> 8) ^ secret_key;
    report[7] = secret_key;
    for (size_t i = 0; i < data_length; i++) report[8 + i] = data[i] ^ secret_key;
    report[3] = checksum(report);

    if (!exchange_report(device, input, report)) return false;
    if (
        input->length != REPORT_SIZE ||
        input->report[0] != NUPHY_READ_COMMAND ||
        input->report[1] != NUPHY_SET_LIGHT_STATE ||
        input->report[3] != checksum(input->report)
    ) {
        fprintf(stderr, "invalid set-light response at address %u\n", address);
        return false;
    }
    return true;
}

int main(int argc, char **argv) {
    Operation operation = OP_READ;
    const char *operation_value = NULL;
    if (argc == 2 && strcmp(argv[1], "--test-green") == 0) {
        operation = OP_TEST_GREEN;
    } else if (
        argc == 1 ||
        (argc == 2 && strcmp(argv[1], "--secure") == 0) ||
        (argc == 2 && strcmp(argv[1], "--get-side") == 0)
    ) {
        operation = OP_READ;
    } else if (argc == 3 && strcmp(argv[1], "--color") == 0) {
        operation = OP_COLOR;
        operation_value = argv[2];
    } else if (argc == 3 && strcmp(argv[1], "--set-side") == 0) {
        operation = OP_SET_SIDE;
        operation_value = argv[2];
    } else {
        fprintf(
            stderr,
            "usage: %s [--get-side | --test-green | --color red|yellow|green | --set-side 16hex]\n",
            argv[0]
        );
        return 64;
    }
    bool test_green = operation == OP_TEST_GREEN;
    uint8_t requested_side_state[8] = {0};
    if (operation == OP_COLOR) {
        const uint8_t red[8] = {2, 100, 1, 0, 0, 255, 0, 0};
        const uint8_t yellow[8] = {2, 100, 1, 0, 0, 255, 180, 0};
        const uint8_t green[8] = {2, 100, 1, 0, 0, 0, 255, 0};
        if (strcmp(operation_value, "red") == 0) {
            memcpy(requested_side_state, red, sizeof(red));
        } else if (strcmp(operation_value, "yellow") == 0) {
            memcpy(requested_side_state, yellow, sizeof(yellow));
        } else if (strcmp(operation_value, "green") == 0) {
            memcpy(requested_side_state, green, sizeof(green));
        } else {
            fprintf(stderr, "unknown color: %s\n", operation_value);
            return 64;
        }
    } else if (
        operation == OP_SET_SIDE &&
        !parse_side_state(operation_value, requested_side_state)
    ) {
        fprintf(stderr, "side state must be exactly 16 hexadecimal characters\n");
        return 64;
    }
    IOHIDManagerRef manager = IOHIDManagerCreate(kCFAllocatorDefault, kIOHIDOptionsTypeNone);
    int vendor = KICK75_VENDOR_ID;
    int product = KICK75_PRODUCT_ID;
    CFNumberRef vendor_number = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &vendor);
    CFNumberRef product_number = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &product);
    const void *keys[] = {CFSTR(kIOHIDVendorIDKey), CFSTR(kIOHIDProductIDKey)};
    const void *values[] = {vendor_number, product_number};
    CFDictionaryRef matching = CFDictionaryCreate(
        kCFAllocatorDefault,
        keys,
        values,
        2,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks
    );
    IOHIDManagerSetDeviceMatching(manager, matching);
    IOReturn manager_result = IOHIDManagerOpen(manager, kIOHIDOptionsTypeNone);
    if (manager_result != kIOReturnSuccess) {
        fprintf(stderr, "manager open warning: 0x%08x (continuing with per-device open)\n", manager_result);
    }

    CFSetRef devices = IOHIDManagerCopyDevices(manager);
    if (!devices) {
        fprintf(stderr, "Kick75 IO not found\n");
        return 2;
    }

    CFIndex count = CFSetGetCount(devices);
    IOHIDDeviceRef device_list[count];
    CFSetGetValues(devices, (const void **)device_list);
    IOHIDDeviceRef raw_device = NULL;
    for (CFIndex i = 0; i < count; i++) {
        IOHIDDeviceRef candidate = device_list[i];
        long usage_page = number_property(candidate, CFSTR(kIOHIDPrimaryUsagePageKey));
        long usage = number_property(candidate, CFSTR(kIOHIDPrimaryUsageKey));
        long max_input = number_property(candidate, CFSTR(kIOHIDMaxInputReportSizeKey));
        long max_output = number_property(candidate, CFSTR(kIOHIDMaxOutputReportSizeKey));
        if (usage_page == 1 && usage == 0 && max_input == REPORT_SIZE && max_output == REPORT_SIZE) {
            raw_device = candidate;
            break;
        }
    }
    if (!raw_device) {
        fprintf(stderr, "NuPhy raw HID interface not found\n");
        return 3;
    }

    IOReturn open_result = IOHIDDeviceOpen(raw_device, kIOHIDOptionsTypeNone);
    if (open_result != kIOReturnSuccess) {
        fprintf(stderr, "raw HID open failed: 0x%08x\n", open_result);
        return 4;
    }

    InputContext input = {0};
    uint8_t callback_buffer[REPORT_SIZE] = {0};
    IOHIDDeviceRegisterInputReportCallback(
        raw_device,
        callback_buffer,
        sizeof(callback_buffer),
        input_callback,
        &input
    );
    IOHIDDeviceScheduleWithRunLoop(raw_device, CFRunLoopGetCurrent(), kCFRunLoopDefaultMode);

    uint8_t secret_key = 0;
    {
        uint8_t secret_report[REPORT_SIZE] = {0};
        secret_report[0] = NUPHY_WRITE_COMMAND;
        secret_report[1] = NUPHY_SET_SECRET_KEY;
        arc4random_buf(secret_report + 8, REPORT_SIZE - 8);
        secret_key = secret_report[28];
        if (secret_key == 0) {
            secret_key = 0xaa;
            secret_report[28] = secret_key;
        }
        secret_report[3] = checksum(secret_report);
        printf("negotiating temporary session key: %02x\n", secret_key);
        if (!exchange_report(raw_device, &input, secret_report)) return 5;
        printf("key response (%ld bytes): ", (long)input.length);
        print_hex(input.report, input.length >= 8 ? 8 : (size_t)input.length);
        if (
            input.length != REPORT_SIZE ||
            input.report[0] != NUPHY_READ_COMMAND ||
            input.report[1] != NUPHY_SET_SECRET_KEY
        ) {
            fprintf(stderr, "unexpected session-key response\n");
            return 6;
        }
    }

    uint8_t query[REPORT_SIZE] = {0};
    query[0] = NUPHY_WRITE_COMMAND;
    query[1] = NUPHY_GET_LIGHT_STATE;
    query[2] = 0;
    query[4] = LIGHT_STATE_SIZE ^ secret_key;
    query[5] = secret_key;
    query[6] = secret_key;
    query[7] = secret_key;
    query[3] = checksum(query);

    printf("query: ");
    print_hex(query, 8);
    if (!exchange_report(raw_device, &input, query)) return 7;

    printf("response (%ld bytes): ", (long)input.length);
    print_hex(input.report, (size_t)input.length);
    if (input.length != REPORT_SIZE) {
        fprintf(stderr, "unexpected response length\n");
        return 8;
    }
    if (input.report[0] != NUPHY_READ_COMMAND || input.report[1] != NUPHY_GET_LIGHT_STATE) {
        fprintf(stderr, "unexpected response header\n");
        return 9;
    }
    uint8_t actual_checksum = checksum(input.report);
    if (input.report[3] != actual_checksum) {
        fprintf(
            stderr,
            "response checksum mismatch: expected %02x, calculated %02x\n",
            input.report[3],
            actual_checksum
        );
        return 10;
    }
    uint8_t decoded_length = input.report[4] ^ secret_key;
    uint16_t decoded_address =
        (uint16_t)(input.report[5] ^ secret_key) |
        (uint16_t)(input.report[6] ^ secret_key) << 8;
    uint8_t decoded_handle = input.report[7] ^ secret_key;
    if (decoded_length != LIGHT_STATE_SIZE || decoded_address != 0 || decoded_handle != 0) {
        fprintf(
            stderr,
            "unexpected decoded response fields: len=%u addr=%u handle=%u\n",
            decoded_length,
            decoded_address,
            decoded_handle
        );
        return 11;
    }
    for (size_t i = 0; i < LIGHT_STATE_SIZE; i++) input.report[8 + i] ^= secret_key;
    printf("light-state payload: ");
    print_hex(input.report + 8, LIGHT_STATE_SIZE);
    print_side_state_machine(input.report + 17);

    if (operation == OP_COLOR || operation == OP_SET_SIDE) {
        if (!set_light_data(
                raw_device,
                &input,
                secret_key,
                9,
                requested_side_state,
                sizeof(requested_side_state)
            )) {
            fprintf(stderr, "failed to update side LEDs\n");
            return 12;
        }
        printf("side LED update acknowledged by keyboard\n");
    }

    if (test_green) {
        uint8_t original_side_state[8] = {0};
        memcpy(original_side_state, input.report + 17, sizeof(original_side_state));
        const uint8_t green_side_state[8] = {2, 100, 1, 0, 0, 0, 255, 0};
        const uint8_t full_brightness[1] = {100};

        printf("saved side state: ");
        print_hex(original_side_state, sizeof(original_side_state));
        printf("setting five side LEDs to static green for 5 seconds...\n");
        bool green_written = set_light_data(
            raw_device,
            &input,
            secret_key,
            9,
            green_side_state,
            sizeof(green_side_state)
        );
        bool green_brightness_written = false;
        if (green_written) {
            green_brightness_written = set_light_data(
                raw_device,
                &input,
                secret_key,
                10,
                full_brightness,
                sizeof(full_brightness)
            );
            usleep(5000000);
        }

        printf("restoring saved side state...\n");
        bool restored = set_light_data(
            raw_device,
            &input,
            secret_key,
            9,
            original_side_state,
            sizeof(original_side_state)
        );
        uint8_t original_brightness[1] = {original_side_state[1]};
        bool brightness_restored = set_light_data(
            raw_device,
            &input,
            secret_key,
            10,
            original_brightness,
            sizeof(original_brightness)
        );
        if (!green_written || !green_brightness_written || !restored || !brightness_restored) {
            fprintf(
                stderr,
                "test status: green=%d green-brightness=%d restore=%d restore-brightness=%d\n",
                green_written,
                green_brightness_written,
                restored,
                brightness_restored
            );
            return 13;
        }
        printf("restore commands acknowledged by keyboard\n");
    }

    IOHIDDeviceUnscheduleFromRunLoop(raw_device, CFRunLoopGetCurrent(), kCFRunLoopDefaultMode);
    IOHIDDeviceClose(raw_device, kIOHIDOptionsTypeNone);
    CFRelease(devices);
    CFRelease(matching);
    CFRelease(vendor_number);
    CFRelease(product_number);
    IOHIDManagerClose(manager, kIOHIDOptionsTypeNone);
    CFRelease(manager);
    return 0;
}
